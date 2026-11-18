# 版本与发布分支约定

当前版本是 **`v0.1.3-beta.4`**。此前所有 `0.1.x` 安装包和便携包，包括没有后缀的版本，均为历史测试产物；保留原名，不补建正式发布。

- 测试版：`vX.Y.Z-beta.N`，首个测试版从 `beta.1` 开始。
- 正式版：`vX.Y.Z`，不带后缀。
- 每个正式版保留分支 `release/vX.Y.Z` 与标签 `vX.Y.Z`，二者指向同一发布提交，归档后不再移动或接收新提交，不强制推送。后续修复使用新版本。

版本来源是 `src/AdbDock.App/AdbDock.App.csproj` 的 `Version`，填写 `X.Y.Z-beta.N` 或 `X.Y.Z`。脚本据此生成包名与版本显示。更新版本时同步两份 README 的安装包下载链接和版本说明。分支与标签名只使用版本号。

## 操作示例

测试版在开发分支修改版本、运行检查并打包，不运行正式归档命令。

正式版从选定的功能提交创建分支，单独提交正式版本号修改，确保最终提交包含且仅包含本次要发布的内容：

```powershell
git fetch origin
git switch -c release/vX.Y.Z <commit>
# 将项目 Version 改为 X.Y.Z。
git add -- src/AdbDock.App/AdbDock.App.csproj
git diff --cached
git commit -m "chore: prepare vX.Y.Z"
git status --short
git rev-parse HEAD
```

在这一干净分支上按 [README](../README.md#development) 运行检查和打包。正式构建要求本地分支与版本匹配、工作区干净，此时无需先推送。

```powershell
.\scripts\BuildInstaller.ps1 -IncludeUiChecks
# 完成 README 中需要的安装流程检查后：
.\scripts\ArchiveStableRelease.ps1
```

`ArchiveStableRelease.ps1` 无需参数，会原子推送 `release/vX.Y.Z` 与标签 `vX.Y.Z` 并核对远端引用，拒绝测试版本。归档成功后再使用这一标签和同一提交生成的安装包、校验文件创建 GitHub 正式发布；发布命令应验证已有标签。已有版本的引用不得覆盖。

## English quick reference

Use `vX.Y.Z-beta.N` for betas, starting at `beta.1`, and `vX.Y.Z` for stable releases. Earlier `0.1.x` packages are legacy test artifacts, including unsuffixed versions; do not rename them or create stable releases retroactively.

For stable releases, create `release/vX.Y.Z` from the chosen functional commit, update and commit the project `Version`, then check the final commit and clean tree. Package names and displayed versions derive from the project version. Update the installer links and release summaries in both README files when preparing a release. Run tests and packaging locally before pushing. After verification, run `scripts/ArchiveStableRelease.ps1` with no arguments. It atomically pushes and verifies the matching branch and tag before publication. Publish the artifacts from that commit against the verified tag. Keep both references frozen and use a new version for later fixes.
