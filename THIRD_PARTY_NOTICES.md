# Third-Party Notices

This repository does not commit third-party executables.

## rclone

PathTwin can optionally bundle `rclone.exe` in Windows release artifacts. rclone is a separate project and is not part of the PathTwin source license.

- Project: https://rclone.org/
- Source: https://github.com/rclone/rclone
- License: MIT
- Local license copy: `third_party/rclone/COPYING`

`tools/rclone.exe` is ignored by Git. If a release artifact includes `rclone.exe`, include this notice and `third_party/rclone/COPYING` in that artifact.
