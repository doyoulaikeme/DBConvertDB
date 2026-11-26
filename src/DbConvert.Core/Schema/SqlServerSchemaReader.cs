using System;
using System.Collections.Generic;
using System.Data.Common;

namespace DbConvert.Schema
{
    /// <summary>SQL Server 结构读取（sys 目录视图）。</summary>
    public class SqlServerSchemaReader : ISchemaReader
    {
        public string ResolveSchema(DbConnection conn, string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            try
            {
                object v = SchemaSql.Scalar(conn, "SELECT SCHEMA_NAME()");
                return v == null ? "dbo" : Convert.ToString(v);
            }
            catch
            {
                return "dbo";
            }
        }

        public List<TableDef> ReadTables(DbConnection conn, string schema, Action<string> log)
        {
            if (string.IsNullOrEmpty(schema)) schema = "dbo";
            string lit = SqlUtil.Literal(schema);
            var tables = new List<TableDef>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT t.name AS table_name, CAST(ep.value AS nvarchar(max)) AS table_comment, " +
                    "  (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0,1)) AS row_estimate " +
                    "FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                    "LEFT JOIN sys.extended_properties ep ON ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description' " +
                    "WHERE s.name = " + lit + " ORDER BY t.name";
                cmd.CommandTimeout = 180;
                using (var rd = cmd.ExecuteReader())
                {
                    var acc = new RowAccess(rd);
                    while (rd.Read())
                    {
                        var t = new TableDef { Schema = schema, Name = acc.Str("table_name") };
                        t.Comment = acc.Str("table_comment") ?? "";
                        long? n = acc.Long("row_estimate");
                        t.RowCountEstimate = n.HasValue ? n.Value : -1;
                        tables.Add(t);
                    }
                }
            }

            var byName = new Dictionary<string, TableDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables) byName[t.Name] = t;

            ReadColumns(conn, schema, byName, log);
            ReadIndexes(conn, schema, byName, log);
            ReadConstraints(conn, schema, byName, log);
            return tables;
        }

        private void ReadColumns(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT t.name AS table_name, c.column_id, c.name AS column_name, ty.name AS data_type, " +
                        "c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, " +
                        "CAST(ep.value AS nvarchar(max)) AS column_comment, dc.definition AS column_default, " +
                        "cc.definition AS computed_definition, cc.is_persisted " +
                        "FROM sys.columns c " +
                        "JOIN sys.tables t ON t.object_id = c.object_id " +
                        "JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                        "JOIN sys.types ty ON ty.user_type_id = c.user_type_id " +
                        "LEFT JOIN sys.extended_properties ep ON ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description' " +
                        "LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id " +
                        "LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id " +
                        "WHERE s.name = " + lit + " ORDER BY t.name, c.column_id";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("table_name");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            var col = new ColumnDef
                            {
                                Name = acc.Str("column_name") ?? "",
                                Ordinal = acc.Int("column_id") ?? 0,
                                Nullable = acc.Bool("is_nullable"),
                                IsIdentity = acc.Bool("is_identity"),
                                IsComputed = acc.Bool("is_computed"),
                                Comment = acc.Str("column_comment") ?? ""
                            };
                            col.TargetName = col.Name;
                            MapType(col, acc.Str("data_type") ?? "", acc.Int("max_length"),
                                    acc.Int("precision"), acc.Int("scale"), acc.Int("scale"));
                            string dv = acc.Str("column_default");
                            if (!string.IsNullOrWhiteSpace(dv) && !col.IsComputed)
                            {
                                col.HasDefault = true;
                                col.DefaultExpression = dv.Trim();
                            }
                            if (col.IsComputed)
                            {
                                string cd = acc.Str("computed_definition");
                                if (!string.IsNullOrWhiteSpace(cd))
                                {
                                    col.GenerationExpression = cd.Trim();
                                    col.GeneratedStored = acc.Bool("is_persisted");
                                }
                                else
                                {
                                    col.IsComputed = false;
                                    col.MappingWarning = "计算列没读到表达式，已按普通列处理并搬数据";
                                }
                            }
                            byName[tn].Columns.Add(col);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 SQL Server 列信息失败：" + ex.Message);
            }
        }

        private void MapType(ColumnDef col, string typeName, int? maxLengthBytes, int? numericPrecision, int? datetimePrecision, int? scale)
        {
            string t = (typeName ?? "").Trim().ToLowerInvariant();
            col.RawType = t;
            col.SourceTypeName = t;

            switch (t)
            {
                case "int":
                    col.Kind = ColumnKind.Int;
                    break;
                case "bigint":
                    col.Kind = ColumnKind.BigInt;
                    break;
                case "smallint":
                    col.Kind = ColumnKind.SmallInt;
                    break;
                case "tinyint":
                    col.Kind = ColumnKind.TinyInt;
                    break;
                case "bit":
                    col.Kind = ColumnKind.Boolean;
                    break;
                case "decimal":
                case "numeric":
                    col.Kind = ColumnKind.Decimal;
                    col.Precision = numericPrecision;
                    col.Scale = scale;
                    break;
                case "money":
                case "smallmoney":
                    col.Kind = ColumnKind.Money;
                    break;
                case "float":
                    col.Kind = maxLengthBytes.HasValue && maxLengthBytes.Value <= 24 ? ColumnKind.Float : ColumnKind.Double;
                    break;
                case "real":
                    col.Kind = ColumnKind.Float;
                    break;
                case "char":
                case "varchar":
                {
                    col.Kind = t == "char" ? ColumnKind.Char : ColumnKind.VarChar;
                    if (maxLengthBytes.HasValue)
                    {
                        col.Length = maxLengthBytes.Value < 0 ? null : maxLengthBytes.Value;
                    }
                    break;
                }
                case "nchar":
                    col.Kind = ColumnKind.NChar;
                    if (maxLengthBytes.HasValue && maxLengthBytes.Value > 0) col.Length = maxLengthBytes.Value / 2;
                    break;
                case "nvarchar":
                    col.Kind = ColumnKind.NVarChar;
                    if (maxLengthBytes.HasValue && maxLengthBytes.Value > 0) col.Length = maxLengthBytes.Value / 2;
                    break;
                case "text":
                case "ntext":
                    col.Kind = ColumnKind.Text;
                    break;
                case "binary":
                    col.Kind = ColumnKind.Binary;
                    if (maxLengthBytes.HasValue && maxLengthBytes.Value > 0) col.Length = maxLengthBytes.Value;
                    break;
                case "varbinary":
                    col.Kind = maxLengthBytes.HasValue && maxLengthBytes.Value > 0 ? ColumnKind.Binary : ColumnKind.Blob;
                    if (col.Kind == ColumnKind.Binary) col.Length = maxLengthBytes.Value;
                    break;
                case "image":
                    col.Kind = ColumnKind.Blob;
                    break;
                case "date":
                    col.Kind = ColumnKind.Date;
                    break;
                case "time":
                    col.Kind = ColumnKind.Time;
                    col.DateTimePrecision = scale;
                    break;
                case "datetime":
                    col.Kind = ColumnKind.DateTime;
                    col.DateTimePrecision = 3;
                    break;
                case "datetime2":
                    col.Kind = ColumnKind.DateTime;
                    col.DateTimePrecision = scale;
                    break;
                case "smalldatetime":
                    col.Kind = ColumnKind.DateTime;
                    col.DateTimePrecision = 0;
                    break;
                case "datetimeoffset":
                    col.Kind = ColumnKind.TimestampTz;
                    col.DateTimePrecision = scale;
                    break;
                case "uniqueidentifier":
                    col.Kind = ColumnKind.Guid;
                    break;
                case "xml":
                    col.Kind = ColumnKind.Xml;
                    break;
                case "sql_variant":
                case "geography":
                case "geometry":
                case "hierarchyid":
                    col.Kind = ColumnKind.Unknown;
                    col.MappingWarning = "SQL Server 类型 " + t + " 暂不支持自动迁移";
                    break;
                default:
                    col.Kind = ColumnKind.Unknown;
                    col.MappingWarning = "未识别的 SQL Server 类型 " + t;
                    break;
            }
        }

        private void ReadIndexes(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT t.name AS table_name, i.name AS index_name, i.is_unique, i.is_primary_key, " +
                        "ic.key_ordinal, c.name AS column_name, ic.is_descending_key " +
                        "FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id " +
                        "JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                        "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                        "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                        "WHERE s.name = " + lit + " AND i.type IN (1,2) AND i.is_hypothetical = 0 AND ic.is_included_column = 0 " +
                        "ORDER BY t.name, i.index_id, ic.key_ordinal";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("table_name");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            TableDef tb = byName[tn];
                            string name = acc.Str("index_name") ?? "";
                            if (string.IsNullOrEmpty(name)) continue;
                            IndexDef ix = null;
                            foreach (var e in tb.Indexes)
                            {
                                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) { ix = e; break; }
                            }
                            if (ix == null)
                            {
                                ix = new IndexDef
                                {
                                    Name = name,
                                    IsUnique = acc.Bool("is_unique"),
                                    IsPrimary = acc.Bool("is_primary_key")
                                };
                                tb.Indexes.Add(ix);
                            }
                            string c = acc.Str("column_name");
                            if (!string.IsNullOrEmpty(c)) ix.Columns.Add(new IndexColumn { Name = c, Descending = acc.Bool("is_descending_key") });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 SQL Server 索引失败：" + ex.Message);
            }
        }

        private void ReadConstraints(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT tp.name AS table_name, fk.name AS fk_name, cp.name AS column_name, " +
                        "tr.name AS ref_table, cr.name AS ref_column, fk.delete_referential_action_desc AS del_rule, " +
                        "fk.update_referential_action_desc AS upd_rule, fkc.constraint_column_id " +
                        "FROM sys.foreign_keys fk " +
                        "JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id " +
                        "JOIN sys.tables tp ON tp.object_id = fk.parent_object_id " +
                        "JOIN sys.schemas sp ON sp.schema_id = tp.schema_id " +
                        "JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id " +
                        "JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id " +
                        "JOIN sys.columns cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id " +
                        "WHERE sp.name = " + lit + " ORDER BY tp.name, fk.name, fkc.constraint_column_id";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("table_name");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            TableDef t = byName[tn];
                            string name = acc.Str("fk_name") ?? "";
                            ForeignKeyDef fk = null;
                            foreach (var e in t.ForeignKeys)
                            {
                                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) { fk = e; break; }
                            }
                            if (fk == null)
                            {
                                fk = new ForeignKeyDef
                                {
                                    Name = name,
                                    RefTable = acc.Str("ref_table") ?? "",
                                    OnDelete = acc.Str("del_rule") ?? "",
                                    OnUpdate = acc.Str("upd_rule") ?? ""
                                };
                                t.ForeignKeys.Add(fk);
                            }
                            string c = acc.Str("column_name");
                            if (!string.IsNullOrEmpty(c)) fk.Columns.Add(c);
                            string rc = acc.Str("ref_column");
                            if (!string.IsNullOrEmpty(rc)) fk.RefColumns.Add(rc);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 SQL Server 外键失败（将不迁移外键）：" + ex.Message);
            }

            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT t.name AS table_name, cc.name AS cc_name, cc.definition FROM sys.check_constraints cc " +
                        "JOIN sys.tables t ON t.object_id = cc.parent_object_id " +
                        "JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = " + lit;
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("table_name");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            string def = acc.Str("definition") ?? "";
                            if (def.StartsWith("(", StringComparison.Ordinal) && def.EndsWith(")", StringComparison.Ordinal))
                                def = def.Substring(1, def.Length - 2);
                            byName[tn].Checks.Add(new CheckDef { Name = acc.Str("cc_name") ?? "", Expression = def });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 SQL Server CHECK 约束失败：" + ex.Message);
            }
        }

        public List<ViewDef> ReadViews(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<ViewDef>();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT v.name, m.definition FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id " +
                        "LEFT JOIN sys.sql_modules m ON m.object_id = v.object_id WHERE s.name = " + SqlUtil.Literal(schema) + " ORDER BY v.name";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            list.Add(new ViewDef
                            {
                                Schema = schema,
                                Name = Convert.ToString(rd[0]),
                                Definition = rd[1] == DBNull.Value ? "" : Convert.ToString(rd[1])
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 SQL Server 视图失败：" + ex.Message);
            }
            return list;
        }

        public List<RoutineDef> ReadRoutines(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<RoutineDef>();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT o.name, o.type, m.definition FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
                        "LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id " +
                        "WHERE s.name = " + SqlUtil.Literal(schema) + " AND o.type IN ('P','FN','IF','TF','FS','FT') ORDER BY o.name";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            string type = Convert.ToString(rd[1]);
                            list.Add(new RoutineDef
                            {
                                Schema = schema,
                                Name = Convert.ToString(rd[0]),
                                Kind = type.StartsWith("P") ? "procedure" : "function",
                                Definition = rd[2] == DBNull.Value ? "" : Convert.ToString(rd[2])
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 SQL Server 存储过程/函数失败：" + ex.Message);
            }
            return list;
        }

        public List<TriggerDef> ReadTriggers(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<TriggerDef>();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT tr.name, OBJECT_NAME(tr.parent_id) AS table_name, m.definition FROM sys.triggers tr " +
                        "JOIN sys.tables t ON t.object_id = tr.parent_id JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                        "LEFT JOIN sys.sql_modules m ON m.object_id = tr.object_id WHERE s.name = " + SqlUtil.Literal(schema) + " ORDER BY tr.name";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            string def = rd[2] == DBNull.Value ? "" : Convert.ToString(rd[2]);
                            list.Add(new TriggerDef
                            {
                                Schema = schema,
                                Name = Convert.ToString(rd[0]),
                                Table = Convert.ToString(rd[1]),
                                Timing = def.IndexOf("INSTEAD OF", StringComparison.OrdinalIgnoreCase) >= 0 ? "INSTEAD OF" :
                                         (def.IndexOf("BEFORE", StringComparison.OrdinalIgnoreCase) >= 0 ? "BEFORE" : "AFTER"),
                                Definition = def
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 SQL Server 触发器失败：" + ex.Message);
            }
            return list;
        }

        public long CountRows(DbConnection conn, string schema, string table)
        {
            var d = Dialects.Get("sqlserver");
            return SchemaSql.CountRows(conn, d.Qualify(schema, table));
        }
    }
}
