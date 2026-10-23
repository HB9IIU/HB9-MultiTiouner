# How to make a release

1. Raise the version in `HB9-MultiTiouner.csproj` (`<Version>0.2.1</Version>`).
   Middle number for a new feature, last number for a fix.
2. Add a section for it at the top of `CHANGELOG.md`.
3. Run `.\make-release.ps1`. It builds the program and makes `dist\HB9-MultiTiouner-v0.2.1-win-x64.zip`.
   (It needs `tools\ffmpeg\ffmpeg.exe` and `tools\mediamtx\mediamtx.exe`; see `NOTICE.md`.)
4. Unzip it somewhere else and try it.
5. Commit, then tag and push:
   ```
   git commit -am "HB9 MultiTiouner 0.2.1"
   git tag -a v0.2.1 -m "HB9 MultiTiouner 0.2.1"
   git push
   git push --tags
   ```
6. Publish the release with the zip:
   ```
   gh release create v0.2.1 dist\HB9-MultiTiouner-v0.2.1-win-x64.zip --title "HB9 MultiTiouner 0.2.1" --notes "What is new: ..."
   ```

**The tag must look like `v0.2.1`** (a `v` and three numbers). The program checks the tags of the published releases on GitHub, and a number higher than its own makes it show "a new version is available". Drafts are ignored. Releases marked as pre-release are counted.
