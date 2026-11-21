# Contributing

The most useful help right now is a **compatibility report**: try the program on your device and
[tell how it went](https://github.com/Steklorez/time-capsule-windows/issues/new?template=compatibility.yml).
English and Russian are both fine.

## Code

- Needs the .NET 10 SDK. `build.cmd` builds `dist\TimeCapsuleDriveSetup.exe`.
- For development, put the settings of your device into `credentials.txt` in the project folder (`key=value`
  lines: `host`, `volume`, `login`, `password`, `drive`). The file is ignored by git.
- Before sending a change that touches reading or writing, run `TimeCapsuleDrive selftest` against a real
  device and say in the pull request which model you used.
- Keep a pull request to one topic.
