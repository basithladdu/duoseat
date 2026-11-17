# Contributing

Short version: fork, branch, `build.bat`, test on two real laptops if you can, then open a PR.

**House rules**
- **One exe, zero installs.** No NuGet, no runtimes beyond the .NET Framework 4.x that every Windows 10/11 ships.
- **C# 5.** That's what the built-in `csc.exe` speaks: no `$"..."`, no `?.`, no `=>` members.
- **Player A's cursor and focus are sacred.** If your change makes A's cursor move or steals A's active window in Shared mode, it's a bug.
- Say in the PR what you tested and on which apps.

**Testing on one machine**

`test\parallel-test.ps1` opens two throwaway windows. It types into one with the real keyboard ("Player A") while a fake Player B (`test\TestDriver.exe`) types into the other over the network, then checks that nothing crossed over. Hands off the mouse and keyboard while it runs.

`test\apps-test.ps1` does the same against an Edge window with its own throwaway profile.

**Good first issues:** more apps in the auto-borrow list (`Injector.cs`, `BorrowClasses`), a cursor-speed slider, a tray icon for Player A.
