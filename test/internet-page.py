# Opens the Player B page through the internet link like a far-away browser. Needs: pip install playwright
import asyncio, sys, time
from playwright.async_api import async_playwright
link, pin = sys.argv[1], sys.argv[2]
async def main():
    async with async_playwright() as p:
        b = await p.chromium.launch(channel="msedge")
        bad = await b.new_page(); await bad.goto(link + "/"); await bad.fill("#pin", "0" * len(pin)); await bad.click("#go")
        await asyncio.sleep(4); print("wrong PIN answer:", await bad.evaluate("document.getElementById('err').textContent"))
        pg = await b.new_page(viewport={"width": 900, "height": 560}); t0 = time.time()
        await pg.goto(link + "/#pin=" + pin)
        for _ in range(60):
            w = await pg.evaluate("document.getElementById('screen').naturalWidth")
            if w: break
            await asyncio.sleep(0.25)
        print("first live frame over the internet: %s px wide after %d ms" % (w, (time.time() - t0) * 1000))
        await pg.evaluate("window.__n=0; new MutationObserver(()=>__n++).observe(document.getElementById('screen'),{attributes:true,attributeFilter:['src']})")
        await asyncio.sleep(4); print("fps over the internet:", await pg.evaluate("__n/4"))
        await b.close()
asyncio.run(main())
