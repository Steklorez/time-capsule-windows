# Time Capsule Drive

**English** | [Русский](README.ru.md)

Use an Apple Time Capsule (or an AirPort disk) on Windows 10/11 as an ordinary drive letter, over Apple's own
file protocol, AFP. No SMB1 needed.

Windows can only reach a Time Capsule through SMB1, an old protocol that Windows 11 ships switched off and that
is slow on this hardware. This program talks to the Time Capsule the way a Mac does and shows it as a normal disk.

> Status: early release. It has been developed and tested against a single device, a Time Capsule 4th generation
> (2011). Other models may behave differently; reports are welcome. Keep a second copy of anything important.

## Download

**[Download TimeCapsuleDriveSetup.exe](https://github.com/Steklorez/time-capsule-windows/releases/latest/download/TimeCapsuleDriveSetup.exe)** (latest version; all versions are on the [Releases](https://github.com/Steklorez/time-capsule-windows/releases) page).

## Install

Run `TimeCapsuleDriveSetup.exe` and confirm the administrator prompt. In the window:

1. **Device on the network** is found automatically; if the list stays empty, type the IP address.
2. **User name and password** as set on the Time Capsule (leave the user name empty for a disk password, both
   empty for guest access).
3. Pick a **drive letter** and, if you want, tick **Share this drive with other computers**.
4. Press **Install**.

The drive appears at once and comes back by itself every time Windows starts, for all users. Running the setup
program again changes the settings or uninstalls; uninstalling also works from Windows "Apps".

If the drive is gone, open the setup window again: the line at the top says whether it is connected and, if not,
why (the device does not answer, the password is refused). If the Time Capsule gets a new IP address, the program
finds it on the network by name and remembers the new address.

Requirements: Windows 10/11 x64. Nothing else to install and no internet needed: .NET and the official WinFsp
installer are inside the file. The file is not code-signed, so Windows SmartScreen will warn about it
("More info" → "Run anyway").

### Two ways to use it on a network

- **On every computer:** install without sharing. Each computer talks to the Time Capsule directly; fastest.
- **One bridge computer:** install with sharing on one PC; the others open `\\bridge\TimeCapsule` as a normal
  network folder and install nothing.

## Speed

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/speed-en-dark.svg">
  <img alt="Speed comparison: AFP drive vs Windows built-in SMB1" src="docs/speed-en-light.svg">
</picture>

Over Wi-Fi:

| | Windows built-in SMB1 | Time Capsule Drive (AFP) | Difference |
|---|---:|---:|---|
| Large file, write | 15.2 MB/s | 25.4 MB/s | 1.7× faster |
| Large file, read | 13.0 MB/s | 28.5 MB/s | 2.2× faster |
| Small files, create | 35 per second | 30 per second | 14% slower |
| Small files, read | 38 per second | 94 per second | 2.5× faster |
| Small files, delete | 13 per second | 87 per second | 6.7× faster |

Over a network cable:

| | Windows built-in SMB1 | Time Capsule Drive (AFP) | Difference |
|---|---:|---:|---|
| Large file, write | 23.0 MB/s | 28.6 MB/s | 1.2× faster |
| Large file, read | 22.8 MB/s | 41.1 MB/s | 1.8× faster |
| Small files, create | 47 per second | 30 per second | 36% slower |
| Small files, read | 39 per second | 117 per second | 3.0× faster |
| Small files, delete | 17 per second | 95 per second | 5.6× faster |

Reading and deleting are much faster; writing a large file is somewhat faster; creating many small files is
currently slower than SMB1.

How it was measured: one Windows 11 laptop, Time Capsule 4th generation, a 400 MB file and 200 files of 10 KB,
Windows' own cache bypassed, averages of three runs of each test (`tools/speed-compare.ps1`); for the cable run
the laptop was on battery. One computer and one device: a guide, not a guarantee. Run the script yourself to see
your numbers.

On another PC over gigabit cable the same drive writes at 31–34 MB/s and reads from 45 MB/s (a file just written)
up to 90 MB/s (a file that has been on the disk for a while), which is the limit of the Time Capsule itself.

The charts are drawn by `docs/make-speed-chart.py`.

## What the installer puts on the computer

| What | Where |
|---|---|
| Program | `C:\Program Files\TimeCapsuleDrive\` (`TimeCapsuleDrive.exe`, `winfsp-msil.dll`) |
| Settings (password encrypted, administrators only) | `C:\ProgramData\TimeCapsuleDrive\config.txt` |
| Log (everyone may read, only administrators may change) | `C:\ProgramData\TimeCapsuleDrive\timecapsule.log` |
| Start with Windows | scheduled task `TimeCapsuleDrive` (runs as SYSTEM at boot) |
| Sharing (if chosen) | a network share with full access (sign in with an account of that computer) and the firewall rule "TimeCapsuleDrive file sharing (SMB-In)": port 445 for the local subnet on private networks. The rule opens file sharing for the whole computer, not only for this share |
| Drive component | WinFsp (left in place on uninstall: other programs may use it) |

## Data safety

Verified on a Time Capsule 4th generation (the `selftest` command and the test scripts):

- **One writer at a time.** A file open for writing cannot be opened for writing by another computer
  ("file in use"); reading it is still allowed.
- **Lost connection.** The program reconnects, the server discards the old session, open files are reopened and
  carry on. Tested under load with the connection cut every few seconds: large and small files arrived intact.
- **Crash of the program or the computer.** On the next start the server closes whatever the previous run left
  open. Without this the Time Capsule keeps such files locked for about seven minutes.
- **Flush means flush.** An application's "write to disk now" request is passed to the server as a real
  commit to the disk.
- **Replacing a file.** When a file is saved over an existing one, the old file is moved aside first and
  removed only after the new one is in place.
- **Large and many.** A 4.3 GB file, three copies at once, 600 files in a row: no errors.

How writes are confirmed: a large write returns to the application only after the server has acknowledged it.
Small writes (under 128 KB) are gathered into bigger pieces first, which is what makes copying with small buffers
fast; they are sent within about two seconds, or when the file is read, flushed or closed. If saving such data
fails, the application is told on its next operation on the file; if the failure happens at close, it is only
written to the log. A full disk is checked for in advance.

Implemented from the protocol documentation but not exercised on a live device:

- **Sleep.** Before the computer sleeps, pending data is written and the server is told the client is asleep.
- **Silent connection.** Two minutes without anything from the server counts as a lost connection (the server
  normally sends a keep-alive every 30 seconds).
- **Server messages** about shutdown and about changes on the disk (the latter refresh the file list).

Known limitations:

- After the connection is cut in the middle of a write, the first reconnect can take up to two minutes; the
  drive pauses meanwhile and then continues.
- With guest access (no user name and password) a file that was open for writing may stay locked for up to
  seven minutes after a lost connection; the server's behaviour for guests has not been verified.
- One installation mounts one disk of one Time Capsule.
- AFP does not authenticate the server. The program connects to the device by address and, if it does not answer
  there, looks on the local network for a device with the same name and model. On someone else's or a public
  network that can be abused, so this is meant for your home network.
- Mac metadata (Finder labels, resource forks of old files) is invisible to Windows and is not carried over when
  Windows copies a file. Moving and renaming inside the disk keep it.
- Byte-range locks are not shared between computers (only whole-file write protection is).
- Do not write inside Time Machine backups.
- If the Time Capsule was unreachable when you signed in to Windows and connects later, Explorer may not show
  the drive until Explorer is restarted.

## Command line

```
TimeCapsuleDrive                      setup window
TimeCapsuleDrive install --config F   install without a window, settings from file F  [--share NAME]
TimeCapsuleDrive uninstall [--silent] uninstall
TimeCapsuleDrive stop                 stop the drive (until Windows starts again)
TimeCapsuleDrive probe | ls | selftest | bench | benchw | mount     diagnostic tools
```

The settings file holds `key=value` lines: `host`, `volume`, `login`, `password`, `drive`, `share`.
During development the program reads `credentials.txt` from the project folder (never committed).

## Building

Needs the .NET 10 SDK.

```
build.cmd
```

The result is `dist\TimeCapsuleDriveSetup.exe`: one file that is both the installer and the program.

## How it works

- `src/TimeCapsuleDrive/Afp`: an AFP 3.3 client over TCP written for this project: DHX2 login, files, write
  protection, reconnect, server messages.
- `src/TimeCapsuleDrive/Fs`: the file system for WinFsp: read-ahead, gathered and pipelined writes.
- `src/TimeCapsuleDrive/Install`: the installer and the setup window.

Time Capsule quirks the code works around:

- creating, deleting and renaming only work when the target is given as "parent folder id + name";
- growing a file makes the Time Capsule fill it with zeros, so growth is postponed until the file is closed;
- its disk goes to sleep; the first access after a pause takes about 8 seconds.

## Licence

Time Capsule Drive is free software, released under the [GNU General Public License v3.0](LICENSE).

## Dependencies

- [WinFsp](https://winfsp.dev): GPLv3 with an exception for free and open-source software that permits both
  linking and redistributing the unmodified installer. The WinFsp 2.2 installer is included as is
  (source: https://github.com/winfsp/winfsp, licence: `third_party/WinFsp-License.txt`).
- [BouncyCastle](https://www.bouncycastle.org/csharp/): MIT (the CAST5 cipher used by the login).

Apple, Time Capsule, AirPort and Time Machine are trademarks of Apple Inc. This project is not affiliated with
or endorsed by Apple.
