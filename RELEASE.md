# Release workflow

正式发版必须从干净的 Git 工作区开始，并且发布产物必须对应一个 Git tag。紧急补包或本机测试可以不传 `-Tag`，脚本会生成一个临时 dev 便携包。

## 规则

- 源码仓库只提交源码、项目文件、文档和发布脚本。
- `bin/`、`obj/`、`Diagnostics/bin/`、`Diagnostics/obj/` 都是可再生构建产物，不能进入 Git。
- 正式发布产物只输出到 `Release/<tag>/`，`Release/` 已被 `.gitignore` 忽略。
- 每个正式发布必须使用 SemVer tag，例如 `v0.2.0-beta.1`。
- 发布脚本在传入 `-Tag` 时会拒绝 dirty working tree，也会拒绝没有 tag 或 tag 不指向 HEAD 的构建。
- 不传 `-Tag` 时会创建 `Release/dev-yyyyMMdd-HHmmss/`，用于临时补包测试。
- 发布脚本会在发布前关闭 .NET build server，并安全清理本次目标的 `bin\Release\<framework>\<runtime>\`、`obj\Release\<framework>\<runtime>\` 和对应 `Release\...` 输出目录。
- 发布脚本会在发布后检查 exe、zip、zip 内文件和 SHA256；portable zip 只应该包含 `AppleMusicTranslator.exe` 和 `README.txt`，不能混入散落的 `.dll`、`.pdb`、`.json`、`.deps`、`.runtimeconfig` 等文件。

## 第一次恢复 Git 工作区

```powershell
git init
git add .
git commit -m "chore: restore source repository"
git tag v0.2.0-beta.1
```

如果之后要推到远端：

```powershell
git remote add origin <repo-url>
git push -u origin main --tags
```

## 发布

临时补包：

```powershell
.\scripts\Publish-Portable.ps1
```

正式发版：

```powershell
.\scripts\Publish-Portable.ps1 -Tag v0.2.0-beta.1
```

输出目录：

```text
Release\v0.2.0-beta.1\
```

GitHub Release 优先上传：

```text
AppleMusicTranslator-v0.2.0-beta.1-win-x64-portable.zip
SHA256SUMS.txt
```

`AppleMusicTranslator-<tag>-win-x64-portable.zip` 是便携压缩包，不是安装器。用户需要解压后运行 `AppleMusicTranslator.exe`。

`portable-win-x64\AppleMusicTranslator.exe` 是同一次构建留下的解包检查件，不需要单独上传。
