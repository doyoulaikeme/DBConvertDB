# DBConvertDB v1.0.0 — .NET Framework 4.8 跨库数据库迁移工具

发布日期：2026-11-26

DBConvertDB 是一个自带迁移引擎的数据库工具，支持在 MySQL、PostgreSQL、Oracle、SQL Server、SQLite 与达梦 DM、金仓 KingbaseES、openGauss、OceanBase、TiDB、GBase 8a、瀚高之间迁移表结构、数据、视图、过程/函数、触发器和约束，并输出类型映射、对象问题清单和迁移报告。

## 本次发布内容

- 目标框架迁移到 **.NET Framework 4.8**：Windows 10/11 自带运行时；Windows 7 SP1 安装 4.8 运行时即可；不再依赖 .NET 5。
- 提供两套入口：`DbConvert.Cli.exe` 命令行和 `DbConvert.Gui.exe` WPF 图形界面，共用同一迁移引擎。
- 支持 12 种数据库类型，覆盖 MySQL 系、PostgreSQL 系、Oracle、SQL Server、SQLite、达梦 DM。
- 支持“直接导入 / 只生成 SQL”双模式：勾选 `ChkScriptOnly` 或 CLI 加 `--script-only`，只生成结构 SQL，不连接目标库、不搬数据。
- 同类型数据库之间直接创建过程/函数/触发器；跨类型数据库生成 `objects_manual_*.sql` 草稿并标记“需人工”。
- 迁移后按表核对行数和逐行校验和，输出 `issues_*.csv`、`mapping_*.csv`、`report_*.md`、`schema_*.sql`。
- 已实测 MySQL → MySQL、SQLite → SQLite 端到端迁移；MySQL 示例包含视图、函数、过程、触发器、外键和 CHECK。

## 典型场景

- MySQL 迁移到达梦 DM / 金仓 KingbaseES
- Oracle 迁移到金仓 / openGauss
- SQL Server 迁移到 PostgreSQL / MySQL
- SQLite 离线库迁移到 MySQL / PostgreSQL
- 医院、政务、内网环境的整库搬迁和 SQL 审核

## 获取与运行

从源码构建：

```powershell
git clone https://github.com/doyoulaikeme/DBConvertDB.git
cd DBConvertDB
dotnet build DBConvertDB.sln -c Release
```

发布成可直接拷贝的目录：

```powershell
.\publish.ps1
```

运行环境：.NET Framework 4.8；构建环境：.NET SDK 8.0 或更高。

## 驱动说明

- MySQL / PostgreSQL / SQL Server / SQLite 驱动由 NuGet 还原，发布时进入 `dist`。
- Oracle `Oracle.ManagedDataAccess` 从 NuGet 还原；公开再分发前需要按 Oracle OTN 条款确认授权。
- 达梦 `DmProvider.dll`、金仓 `Kdbndp.dll` 等厂商驱动受授权限制，**不随公开仓库或 Release 分发**；请从厂商安装包或官网获取，并放到 `dist\providers` 或程序目录。
- 本 Release 只提供源码；二进制和厂商驱动不随 Release 上传。
- 详细文件清单见 [providers/README.md](../providers/README.md)。

## 已知限制

- 跨语法家族的过程/函数/触发器仍以生成草稿为主，不保证自动改写。
- Oracle/达梦的 `ALL_SOURCE` 只能拿到正文时，无法直接创建对象。
- 表达式索引、部分索引、空间类型会跳过并登记警告。
- 并行度目前固定为 1。
- 达梦、金仓、openGauss、Oracle 的真实联调需要现场库和驱动；本版本重点验证 MySQL、SQLite。

## 许可证

MIT License，详见 [LICENSE](../LICENSE)。
