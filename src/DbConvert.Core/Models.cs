using System;
using System.Collections.Generic;

namespace DbConvert
{
    /// <summary>与具体数据库无关的列语义分类。</summary>
    public enum ColumnKind
    {
        Unknown = 0,
        Boolean,
        TinyInt,
        SmallInt,
        Int,
        BigInt,
        Decimal,
        Float,
        Double,
        Money,
        Char,
        NChar,
        VarChar,
        NVarChar,
        Text,
        Binary,
        Blob,
        Date,
        Time,
        DateTime,
        TimestampTz,
        Guid,
        Json,
        Xml,
        Interval,
        Enum
    }

    public class ColumnDef
    {
        public string Name { get; set; } = "";
        /// <summary>迁移到目标库后使用的列名（默认与 Name 相同）。</summary>
        public string TargetName { get; set; } = "";
        /// <summary>人工指定的目标类型，优先于自动映射。</summary>
        public string TypeOverride { get; set; } = "";
        /// <summary>计算出来的目标类型（由方言映射或人工指定）。</summary>
        public string TargetType { get; set; } = "";
        public string RawType { get; set; } = "";
        public ColumnKind Kind { get; set; } = ColumnKind.Unknown;

        /// <summary>字符长度（字符数，不是字节）。</summary>
        public int? Length { get; set; }
        /// <summary>源库声明的是字节长度（Oracle/DM 的 VARCHAR2(10 BYTE)）。</summary>
        public bool LengthIsBytes { get; set; }

        public int? Precision { get; set; }
        public int? Scale { get; set; }
        public int? DateTimePrecision { get; set; }
        public bool IsUnsigned { get; set; }
        public bool Nullable { get; set; } = true;
        public bool IsIdentity { get; set; }
        public bool IsComputed { get; set; }
        /// <summary>生成列表达式（MySQL GENERATED / PG GENERATED ALWAYS AS / SQL Server computed）。</summary>
        public string GenerationExpression { get; set; } = "";
        /// <summary>生成列是否物化存储（MySQL STORED / PG STORED）。</summary>
        public bool GeneratedStored { get; set; }
        /// <summary>源库默认值原文，字符串会带引号，函数保持原样。</summary>
        public string DefaultExpression { get; set; } = "";
        public bool HasDefault { get; set; }
        public string Charset { get; set; } = "";
        public string Collation { get; set; } = "";
        public string Comment { get; set; } = "";
        public int Ordinal { get; set; }
        /// <summary>源库原始类型名，便于生成迁移报告。</summary>
        public string SourceTypeName { get; set; } = "";
        /// <summary>无法可靠映射时记录原因。</summary>
        public string MappingWarning { get; set; } = "";

        public ColumnDef Clone()
        {
            return (ColumnDef)MemberwiseClone();
        }

        public override string ToString()
        {
            return Name + " " + RawType;
        }
    }

    public class IndexColumn
    {
        public string Name { get; set; } = "";
        public bool Descending { get; set; }
        public override string ToString() => Descending ? Name + " DESC" : Name;
    }

    public class IndexDef
    {
        public string Name { get; set; } = "";
        public bool IsUnique { get; set; }
        public bool IsPrimary { get; set; }
        public List<IndexColumn> Columns { get; set; } = new List<IndexColumn>();
        public override string ToString() => Name + " (" + string.Join(", ", Columns) + ")";
    }

    public class ForeignKeyDef
    {
        public string Name { get; set; } = "";
        public List<string> Columns { get; set; } = new List<string>();
        public string RefSchema { get; set; } = "";
        public string RefTable { get; set; } = "";
        public List<string> RefColumns { get; set; } = new List<string>();
        /// <summary>源库原文：CASCADE / SET NULL / NO ACTION 等。</summary>
        public string OnDelete { get; set; } = "";
        public string OnUpdate { get; set; } = "";
        public override string ToString() => Name + " (" + string.Join(", ", Columns) + ") → " + RefTable;
    }

    public class CheckDef
    {
        public string Name { get; set; } = "";
        public string Expression { get; set; } = "";
        public override string ToString() => Name + " CHECK (" + Expression + ")";
    }

    public class ViewDef
    {
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";
        public string Definition { get; set; } = "";
        public string Comment { get; set; } = "";
        public bool IsMaterialized { get; set; }
        public string TargetSchema { get; set; } = "";
        public string TargetName { get; set; } = "";

        public string QualifiedName()
        {
            return string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;
        }
    }

    public class RoutineDef
    {
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>procedure / function / package</summary>
        public string Kind { get; set; } = "procedure";
        /// <summary>完整源库 DDL 原文。</summary>
        public string Definition { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string ReturnsType { get; set; } = "";
    }

    public class TriggerDef
    {
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";
        public string Table { get; set; } = "";
        /// <summary>BEFORE / AFTER / INSTEAD OF</summary>
        public string Timing { get; set; } = "";
        /// <summary>INSERT / UPDATE / DELETE</summary>
        public string Event { get; set; } = "";
        /// <summary>完整源库 DDL 原文。</summary>
        public string Definition { get; set; } = "";
    }

    public class TableDef
    {
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";
        public string Comment { get; set; } = "";
        public List<ColumnDef> Columns { get; set; } = new List<ColumnDef>();
        public List<IndexDef> Indexes { get; set; } = new List<IndexDef>();
        public List<ForeignKeyDef> ForeignKeys { get; set; } = new List<ForeignKeyDef>();
        public List<CheckDef> Checks { get; set; } = new List<CheckDef>();
        public long RowCountEstimate { get; set; } = -1;

        /// <summary>配置里的目标表名（可能只写表名）。</summary>
        public string TargetSchema { get; set; } = "";
        public string TargetName { get; set; } = "";

        public string QualifiedName => string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;

        public IndexDef PrimaryKey
        {
            get
            {
                foreach (var ix in Indexes)
                {
                    if (ix.IsPrimary) return ix;
                }
                return null;
            }
        }

        public ColumnDef FindColumn(string name)
        {
            foreach (var c in Columns)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        /// <summary>可以读写数据的列（生成列/计算列由目标库自己算，不参与搬运和校验）。</summary>
        public List<ColumnDef> DataColumns()
        {
            var list = new List<ColumnDef>();
            foreach (ColumnDef c in Columns)
            {
                if (c.IsComputed) continue;
                if (string.IsNullOrEmpty(c.TargetType)) continue;
                list.Add(c);
            }
            return list;
        }

        public override string ToString() => QualifiedName;
    }

    // ---------------------------------------------------------------------
    // 配置模型
    // ---------------------------------------------------------------------

    public class EndpointConfig
    {
        public string Kind { get; set; } = "";
        public string ConnectionString { get; set; } = "";
        /// <summary>连接串为空时用这些字段拼装，GUI 里更好填。</summary>
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Database { get; set; } = "";
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
        /// <summary>可选：外部驱动 DLL 路径（达梦 DmProvider.dll、金仓 Kdbndp.dll 等）。</summary>
        public string ProviderAssembly { get; set; } = "";
        /// <summary>可选：驱动工厂类型全名，如 Dm.DmClientFactory。</summary>
        public string ProviderType { get; set; } = "";
        /// <summary>可选：默认 schema / owner / database。</summary>
        public string Schema { get; set; } = "";
    }

    public class ColumnOverride
    {
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public string TargetType { get; set; } = "";
        public bool? Nullable { get; set; }
        public bool? Identity { get; set; }
        public string Comment { get; set; } = "";
    }

    public class TableMapConfig
    {
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public string Where { get; set; } = "";
        public List<string> Columns { get; set; } = new List<string>();
        public List<string> SkipColumns { get; set; } = new List<string>();
        /// <summary>源库没有暴露自增信息时，在这里指定自增列名。</summary>
        public List<string> Identity { get; set; } = new List<string>();
        public List<ColumnOverride> Overrides { get; set; } = new List<ColumnOverride>();
    }

    public class OptionsConfig
    {
        /// <summary>both | schema | data | verify</summary>
        public string Mode { get; set; } = "both";
        public bool DropExisting { get; set; }
        public bool TruncateTarget { get; set; }
        public bool CreateTable { get; set; } = true;
        public bool CreateIfNotExists { get; set; }
        public bool CreateIndexes { get; set; } = true;
        public bool CreateIndexesAfterLoad { get; set; } = true;
        /// <summary>是否把源库的列默认值带到目标库（全对象复现时默认开）。</summary>
        public bool IncludeDefaults { get; set; } = true;
        public int BatchSize { get; set; } = 2000;
        public int Parallel { get; set; } = 1;
        public int CommandTimeout { get; set; } = 0;
        public bool ContinueOnError { get; set; }
        /// <summary>chars | bytes —— 目标库 VARCHAR 长度语义。</summary>
        public string VarcharLengthMode { get; set; } = "auto";
        public int BytesPerChar { get; set; } = 3;
        /// <summary>SQL Server 目标统一用 NVARCHAR/NCHAR，避免中文在不支持 Unicode 的排序规则下变成问号。</summary>
        public bool SqlServerUnicode { get; set; } = true;
        /// <summary>preserve | upper | lower</summary>
        public string IdentifierCase { get; set; } = "auto";
        public bool EmptyStringAsNull { get; set; }
        /// <summary>fail | truncate | skipRow</summary>
        public string OverflowPolicy { get; set; } = "fail";
        /// <summary>auto | date | timestamp</summary>
        public string DateTimePolicy { get; set; } = "auto";
        /// <summary>auto | identity | sequence | none</summary>
        public string IdentityMode { get; set; } = "auto";
        /// <summary>exact | estimate | none</summary>
        public string ProgressCount { get; set; } = "estimate";
        /// <summary>checksum | count | none</summary>
        public string VerifyMode { get; set; } = "checksum";
        public int VerifySamplePercent { get; set; }
        public string Include { get; set; } = "*";
        public string Exclude { get; set; } = "";
        public string OutDir { get; set; } = "";
        public string StateFile { get; set; } = "";
        /// <summary>续传：目标表已有前 N 行时，按单列主键 keyset 续写。</summary>
        public bool Resume { get; set; }
        public bool SkipUnsupportedColumns { get; set; } = true;

        // ---- 全对象迁移（默认开启，只影响 schema 阶段）----
        public bool IncludeViews { get; set; } = true;
        public bool IncludeForeignKeys { get; set; } = true;
        public bool IncludeChecks { get; set; } = true;
        /// <summary>抽取存储过程/函数/触发器源码并生成改造草稿（不自动执行）。</summary>
        public bool IncludeRoutines { get; set; } = true;
        public bool IncludeTriggers { get; set; } = true;
        /// <summary>同类型库之间，过程/函数/触发器取得完整 DDL 后直接写入目标库；跨类型仍生成草稿。</summary>
        public bool DirectObjects { get; set; } = true;
        /// <summary>只生成 SQL/报告，不连接目标库、不搬数据；用于交付给 DBA 审核。</summary>
        public bool ScriptOnly { get; set; }
        /// <summary>为 true 时，视图/外键/CHECK 失败直接中止，而不是记录问题继续。</summary>
        public bool StrictObjects { get; set; }
    }

    public class MigrationConfig
    {
        public EndpointConfig Source { get; set; } = new EndpointConfig();
        public EndpointConfig Target { get; set; } = new EndpointConfig();
        public OptionsConfig Options { get; set; } = new OptionsConfig();
        public List<TableMapConfig> Tables { get; set; } = new List<TableMapConfig>();
    }

    /// <summary>单表迁移/校验的统计结果。</summary>
    public class TableResult
    {
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public long SourceRows { get; set; } = -1;
        public long RowsCopied { get; set; }
        public long RowsSkipped { get; set; }
        public long TargetRows { get; set; } = -1;
        public string ChecksumSource { get; set; } = "";
        public string ChecksumTarget { get; set; } = "";
        public bool Verified { get; set; }
        public string Status { get; set; } = "pending";
        public string Message { get; set; } = "";
        public TimeSpan Elapsed { get; set; }
        public string DdlFile { get; set; } = "";
        public List<string> Warnings { get; set; } = new List<string>();

        public bool Ok => Status == "ok" || Status == "verified" || Status == "skipped" || Status == "planned";
    }

    public class MigrationReport
    {
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public DateTime FinishedAt { get; set; }
        public MigrationConfig Config { get; set; }
        public List<TableResult> Tables { get; set; } = new List<TableResult>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();

        public long TotalRowsCopied
        {
            get
            {
                long n = 0;
                foreach (var t in Tables) n += t.RowsCopied;
                return n;
            }
        }

        public bool Success
        {
            get
            {
                foreach (var t in Tables) if (!t.Ok) return false;
                return Errors.Count == 0;
            }
        }
    }
}

