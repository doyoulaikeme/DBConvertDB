# Contributing

感谢你愿意改进 DBConvertDB。

## 提交 Issue

请尽量附上以下信息：

- 源库/目标库类型与版本（例如 MySQL 8.0.36 → 达梦 DM8）。
- 使用的入口（GUI / CLI）和配置里的 `kind`，不要贴真实密码。
- 复现步骤、报错日志，以及 `issues_*.csv` / `report_*.md` 中脱敏后的关键片段。
- 如果问题与过程、触发器、生成列有关，请附上源库对象的建表/建过程 SQL。

## 提交 Pull Request

1. Fork 仓库并从 `main` 新建分支。
2. 保证 `dotnet build DBConvertDB.sln -c Release` 通过。
3. 尽量为改动补充可复现的示例配置或 SQL；跨库方言改动请说明测试过的数据库版本。
4. 不要提交 `bin/obj/dist/output`、真实数据库文件、连接密码、证书或厂商授权驱动。

提交前请确认目标框架仍是 `net48`，运行环境是 .NET Framework 4.8（Win7 SP1 / 10 / 11），构建建议使用 .NET SDK 8.0。

```powershell
git checkout -b fix/your-change
dotnet build DBConvertDB.sln -c Release
git commit -m "fix: ..."
git push origin fix/your-change
```

提交代码即表示同意按本仓库的 MIT License 授权。
