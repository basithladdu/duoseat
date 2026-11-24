// Encryption for every DuoSeat connection (app and browser, Wi-Fi and internet link).
//
// Key exchange: SPAKE2 in the 2048-bit MODP group (RFC 3526 #14), with the PIN (or the watch code) as
// the password. Someone listening on the Wi-Fi learns nothing, and someone who fakes either side gets
// exactly one PIN guess per try against Player A, which the lockout counts (Guard, Extras.cs). The PIN itself never crosses the network.
// Records: ChaCha20 (RFC 8439) + HMAC-SHA256 (16-byte tag) over a per-direction sequence number,
// so nothing can be read, changed, replayed or reordered. web/seal.js is the same thing for browsers.
using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace DuoSeat
{
    static class Spake
    {
        public static readonly BigInteger P = BigInteger.Parse("00" +
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
            "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
            "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
            "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
            "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
            "15728E5A8AACAA68FFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber);
        public static readonly BigInteger Q = (P - 1) / 2;
        static readonly BigInteger G = 2;
        static readonly BigInteger M = ToGroup("DuoSeat SPAKE2 M"), N = ToGroup("DuoSeat SPAKE2 N");

        // a group element nobody knows the discrete log of: a hash, squared (squares are the order-Q subgroup)
        static BigInteger ToGroup(string label)
        {
            byte[] wide = new byte[9 * 32];
            using (SHA256 sha = SHA256.Create())
                for (int i = 0; i < 9; i++)
                    sha.ComputeHash(Cat(Encoding.ASCII.GetBytes(label), new[] { (byte)i })).CopyTo(wide, i * 32);
            BigInteger h = FromBE(wide) % P;
            return h * h % P;
        }

        // N^w / M^w and their inverses depend only on the PIN, so work them out once per PIN. Then each
        // connection only does exponentiations with fresh random exponents, and timing it says nothing about the PIN.
        static readonly System.Collections.Generic.Dictionary<string, BigInteger[]> masks = new System.Collections.Generic.Dictionary<string, BigInteger[]>();

        static BigInteger[] Masks(bool host, byte[] pwHash, BigInteger w)
        {
            string key = (host ? "h" : "c") + Hex(pwHash);
            lock (masks)
            {
                BigInteger[] m;
                if (masks.TryGetValue(key, out m)) return m;
                if (masks.Count > 16) masks.Clear();
                m = new[] { BigInteger.ModPow(host ? N : M, w, P), BigInteger.ModPow(host ? M : N, Q - w, P) };
                masks[key] = m;
                return m;
            }
        }

        public class Party
        {
            readonly bool host;
            readonly BigInteger w, secretExp;
            readonly byte[] pwHash;
            readonly BigInteger unmask;           // (M or N)^-w, for the other side's number
            public readonly byte[] Mine;      // X (from Player B) or Y (from Player A), 256 bytes
            byte[] transcript;

            // kind: "p" = Player B with the PIN, "v" = a viewer with the watch code
            public Party(bool isHost, string kind, string secret)
            {
                host = isHost;
                using (SHA256 sha = SHA256.Create()) pwHash = sha.ComputeHash(Encoding.UTF8.GetBytes("DuoSeat2 pw " + kind + ":" + secret));
                w = FromBE(pwHash) % Q;
                byte[] r = new byte[32];
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(r);
                secretExp = FromBE(r) % Q;
                if (secretExp.IsZero) secretExp = 1;
                BigInteger[] m = Masks(host, pwHash, w);
                unmask = m[1];
                BigInteger mine = BigInteger.ModPow(G, secretExp, P) * m[0] % P;
                Mine = ToBE(mine);
            }

            // false if the other side's number is junk
            public bool Finish(byte[] theirs)
            {
                if (theirs == null || theirs.Length != 256) return false;
                BigInteger t = FromBE(theirs);
                if (t <= 1 || t >= P - 1) return false;
                BigInteger k = BigInteger.ModPow(t * unmask % P, secretExp * 2, P);    // x2 drops any order-2 part
                if (k <= 1) return false;
                byte[] x = host ? theirs : Mine, y = host ? Mine : theirs;
                using (SHA256 sha = SHA256.Create())
                    transcript = sha.ComputeHash(Cat(Encoding.ASCII.GetBytes("DuoSeat2"), x, y, ToBE(k), pwHash));
                return true;
            }

            public byte[] ConfirmA { get { return Hmac(transcript, "A confirm"); } }
            public byte[] ConfirmB { get { return Hmac(transcript, "B confirm"); } }

            public Box MakeBox()
            {
                byte[] kBA = Hmac(transcript, "B to A key"), mBA = Hmac(transcript, "B to A mac");
                byte[] kAB = Hmac(transcript, "A to B key"), mAB = Hmac(transcript, "A to B mac");
                return host ? new Box(kAB, mAB, kBA, mBA) : new Box(kBA, mBA, kAB, mAB);
            }
        }

        public static byte[] Hmac(byte[] key, string label)
        {
            using (HMACSHA256 h = new HMACSHA256(key)) return h.ComputeHash(Encoding.ASCII.GetBytes(label));
        }

        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        public static byte[] Cat(params byte[][] parts)
        {
            int n = 0; foreach (byte[] p in parts) n += p.Length;
            byte[] r = new byte[n]; int o = 0;
            foreach (byte[] p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }

        public static BigInteger FromBE(byte[] be)
        {
            byte[] le = new byte[be.Length + 1];
            for (int i = 0; i < be.Length; i++) le[i] = be[be.Length - 1 - i];
            return new BigInteger(le);
        }

        public static byte[] ToBE(BigInteger v)
        {
            byte[] le = v.ToByteArray(), be = new byte[256];
            for (int i = 0; i < le.Length && i < 256; i++) be[255 - i] = le[i];
            return be;
        }

        public static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] Unhex(string s)
        {
            if (s == null || s.Length % 2 != 0) return null;
            byte[] r = new byte[s.Length / 2];
            for (int i = 0; i < r.Length; i++)
            {
                int hi = Nib(s[2 * i]), lo = Nib(s[2 * i + 1]);
                if (hi < 0 || lo < 0) return null;
                r[i] = (byte)(hi * 16 + lo);
            }
            return r;
        }
        static int Nib(char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; }
    }

    // Seals and opens records. Each direction has its own keys and counter.
    class Box
    {
        readonly byte[] sendKey, recvKey;
        readonly HMACSHA256 sendMac, recvMac;
        long sendSeq, recvSeq;
        readonly object sl = new object(), rl = new object();

        public Box(byte[] sk, byte[] sm, byte[] rk, byte[] rm)
        {
            sendKey = sk; recvKey = rk;
            sendMac = new HMACSHA256(sm); recvMac = new HMACSHA256(rm);
        }

        public byte[] Seal(byte[] plain, int off, int count)
        {
            lock (sl)
            {
                byte[] seq = BitConverter.GetBytes(sendSeq++);
                byte[] outp = new byte[count + 16];
                Buffer.BlockCopy(plain, off, outp, 0, count);
                ChaCha20.Xor(sendKey, seq, outp, 0, count);
                sendMac.Initialize();
                sendMac.TransformBlock(seq, 0, 8, null, 0);
                sendMac.TransformFinalBlock(outp, 0, count);
                Buffer.BlockCopy(sendMac.Hash, 0, outp, count, 16);
                return outp;
            }
        }

        public byte[] Seal(byte[] plain) { return Seal(plain, 0, plain.Length); }

        // null = tampered, replayed or not for us
        public byte[] Open(byte[] rec)
        {
            if (rec == null || rec.Length < 16) return null;
            lock (rl)
            {
                int n = rec.Length - 16;
                byte[] seq = BitConverter.GetBytes(recvSeq);
                recvMac.Initialize();
                recvMac.TransformBlock(seq, 0, 8, null, 0);
                recvMac.TransformFinalBlock(rec, 0, n);
                byte[] tag = new byte[16]; Buffer.BlockCopy(rec, n, tag, 0, 16);
                byte[] want = new byte[16]; Buffer.BlockCopy(recvMac.Hash, 0, want, 0, 16);
                if (!Spake.Same(tag, want)) return null;
                recvSeq++;
                byte[] plain = new byte[n];
                Buffer.BlockCopy(rec, 0, plain, 0, n);
                ChaCha20.Xor(recvKey, seq, plain, 0, n);
                return plain;
            }
        }
    }

    static class ChaCha20
    {
        // nonce = 4 zero bytes + 8-byte record number, block counter from 0
        public static void Xor(byte[] key, byte[] seq8, byte[] data, int off, int count)
        {
            uint[] st = new uint[16], x = new uint[16];
            st[0] = 0x61707865; st[1] = 0x3320646e; st[2] = 0x79622d32; st[3] = 0x6b206574;
            for (int i = 0; i < 8; i++) st[4 + i] = BitConverter.ToUInt32(key, i * 4);
            st[12] = 0; st[13] = 0;
            st[14] = BitConverter.ToUInt32(seq8, 0); st[15] = BitConverter.ToUInt32(seq8, 4);
            byte[] ks = new byte[64];
            for (int pos = 0; pos < count; pos += 64)
            {
                Array.Copy(st, x, 16);
                for (int r = 0; r < 10; r++)
                {
                    QR(x, 0, 4, 8, 12); QR(x, 1, 5, 9, 13); QR(x, 2, 6, 10, 14); QR(x, 3, 7, 11, 15);
                    QR(x, 0, 5, 10, 15); QR(x, 1, 6, 11, 12); QR(x, 2, 7, 8, 13); QR(x, 3, 4, 9, 14);
                }
                for (int i = 0; i < 16; i++)
                {
                    uint v = x[i] + st[i];
                    ks[i * 4] = (byte)v; ks[i * 4 + 1] = (byte)(v >> 8); ks[i * 4 + 2] = (byte)(v >> 16); ks[i * 4 + 3] = (byte)(v >> 24);
                }
                int n = Math.Min(64, count - pos);
                for (int i = 0; i < n; i++) data[off + pos + i] ^= ks[i];
                st[12]++;
            }
        }

        public static void Block(byte[] key, uint counter, byte[] nonce12, byte[] out64)
        {
            byte[] d = new byte[64];
            uint[] st = new uint[16];
            // test hook for the RFC 8439 vector (full 96-bit nonce)
            st[0] = 0x61707865; st[1] = 0x3320646e; st[2] = 0x79622d32; st[3] = 0x6b206574;
            for (int i = 0; i < 8; i++) st[4 + i] = BitConverter.ToUInt32(key, i * 4);
            st[12] = counter;
            for (int i = 0; i < 3; i++) st[13 + i] = BitConverter.ToUInt32(nonce12, i * 4);
            uint[] x = (uint[])st.Clone();
            for (int r = 0; r < 10; r++)
            {
                QR(x, 0, 4, 8, 12); QR(x, 1, 5, 9, 13); QR(x, 2, 6, 10, 14); QR(x, 3, 7, 11, 15);
                QR(x, 0, 5, 10, 15); QR(x, 1, 6, 11, 12); QR(x, 2, 7, 8, 13); QR(x, 3, 4, 9, 14);
            }
            for (int i = 0; i < 16; i++) BitConverter.GetBytes(x[i] + st[i]).CopyTo(out64, i * 4);
        }

        static void QR(uint[] x, int a, int b, int c, int d)
        {
            x[a] += x[b]; x[d] ^= x[a]; x[d] = (x[d] << 16) | (x[d] >> 16);
            x[c] += x[d]; x[b] ^= x[c]; x[b] = (x[b] << 12) | (x[b] >> 20);
            x[a] += x[b]; x[d] ^= x[a]; x[d] = (x[d] << 8) | (x[d] >> 24);
            x[c] += x[d]; x[b] ^= x[c]; x[b] = (x[b] << 7) | (x[b] >> 25);
        }
    }

    // The app-to-app link after the handshake: every Write becomes one sealed record.
    //   [int32 length][ciphertext][16-byte tag]
    class SecureStream : Stream
    {
        readonly Stream inner;
        readonly Box box;
        readonly object wl = new object();
        byte[] pending; int pendingPos;

        public SecureStream(Stream s, Box b) { inner = s; box = b; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (wl)   // seal and send in the same order
            {
                byte[] rec = box.Seal(buffer, offset, count);
                byte[] all = new byte[4 + rec.Length];
                BitConverter.GetBytes(rec.Length).CopyTo(all, 0);
                Buffer.BlockCopy(rec, 0, all, 4, rec.Length);
                inner.Write(all, 0, all.Length);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (pending == null || pendingPos >= pending.Length)
            {
                byte[] head = new byte[4];
                if (!Fill(head)) return 0;
                int len = BitConverter.ToInt32(head, 0);
                if (len < 16 || len > 48 * 1024 * 1024) throw new IOException("bad record");
                byte[] rec = new byte[len];
                if (!Fill(rec)) return 0;
                pending = box.Open(rec);
                if (pending == null) throw new IOException("tampered record");
                pendingPos = 0;
            }
            int n = Math.Min(count, pending.Length - pendingPos);
            Buffer.BlockCopy(pending, pendingPos, buffer, offset, n);
            pendingPos += n;
            return n;
        }

        bool Fill(byte[] b)
        {
            int got = 0;
            while (got < b.Length)
            {
                int n = inner.Read(b, got, b.Length - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { inner.Flush(); }
        public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); }
        public override void SetLength(long v) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
