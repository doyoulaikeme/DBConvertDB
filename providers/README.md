# 数据库驱动放置说明

本目录**不提交任何厂商 DLL**。原因是达梦、金仓、GBase、神通等驱动通常受厂商授权限制，不能随公开仓库分发；Oracle 驱动也需要按 Oracle 的 OTN 条款确认再分发权限。

## 已由 NuGet 自动还原

以下驱动在 `dotnet restore` / `dotnet build` 时自动下载，发布时会进入 `dist`：

| 数据库 | 主要文件 | NuGet 包 |
|---|---|---|
| MySQL / TiDB / OceanBase / GBase 8a（MySQL 协议） | `MySqlConnector.dll` | `MySqlConnector` |
| PostgreSQL / openGauss / 金仓（PG 协议）/ 瀚高 | `Npgsql.dll` | `Npgsql` |
| SQL Server | `Microsoft.Data.SqlClient.dll`、`Microsoft.Data.SqlClient.SNI.*.dll` | `Microsoft.Data.SqlClient` |
| SQLite | `Microsoft.Data.Sqlite.dll`、`e_sqlite3.dll` | `Microsoft.Data.Sqlite` |
| Oracle | `Oracle.ManagedDataAccess.dll` | `Oracle.ManagedDataAccess`（注意 OTN 授权） |

## 需要自行从厂商获取

把 DLL 放到本目录或程序目录即可；如果工具无法自动识别，可在配置里用 `providerAssembly` / `providerType` 指定。

| 数据库 | 文件名 | 获取方式 |
|---|---|---|
| 达梦 DM | `DmProvider.dll` 或 `DmProvider.Core.dll` | 达梦安装目录的驱动目录，或达梦官网下载中心 |
| 金仓 KingbaseES | `Kdbndp.dll` 或 `KingbaseDataProvider.dll` | 金仓安装目录，或金仓官网下载中心 |
| GBase 8a | 厂商 ADO.NET Provider | 按 GBase 官方文档获取 |
| 神通 / 其他 | 厂商 ADO.NET Provider | 按厂商文档获取，必要时配置 `providerAssembly` |

## 不要把 DLL 提交到 Git

`.gitignore` 会忽略本目录下的 DLL。请只提交驱动说明和获取方式；如果确有厂商的书面再分发授权，再单独确认能否放入 GitHub Release。

开源驱动也不需要提交二进制，`dotnet restore` 会自动拉取；`publish.ps1` 会把它们放进 `dist`。
