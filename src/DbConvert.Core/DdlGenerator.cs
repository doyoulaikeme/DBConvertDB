using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DbConvert
{
    public class DdlStatement
    {
        public string Sql { get; set; } = "";
        /// <summary>create-table / drop-table / comment / index / sequence / trigger</summary>
        public string Kind { get; set; } = "";
        public string Table { get; set; } = "";
        public bool Critical { get; set; }
    }

    /// <summary>按目标方言生成建表、注释、索引脚本。</summary>
    public class DdlGenerator
    {
        private readonly Dialect _d;
        private readonly Dialect _src;
        private readonly OptionsConfig _opt;
        private readonly List<string> _warnings;
        private readonly IssueLog _issues;
        private NameMap _names;

        public DdlGenerator(Dialect dialect, OptionsConfig opt, List<string> warnings,
                            Dialect source = null, IssueLog issues = null)
        {
            _d = dialect;
            _src = source;
            _opt = opt ?? new OptionsConfig();
            _warnings = warnings ?? new List<string>();
            _issues = issues;
        }

        /// <summary>源表名 → 目标表名映射，用于改写视图/外键里的引用。</summary>
        public NameMap Names
        {
            get { return _names ?? (_names = new NameMap()); }
            set { _names = value; }
        }

        private string RewriteExpr(string expr, string objectType, string objectName)
        {
            if (string.IsNullOrEmpty(expr) || _src == null) return expr;
            return SqlRewriter.Rewrite(expr, _src, _d, Names, _issues, objectType, objectName);
        }

        public string TableRef(TableDef t)
        {
            return _d.Qualify(t.TargetSchema, t.TargetName);
        }

        public List<DdlStatement> Build(TableDef t, bool includeDrop)
        {
            var list = new List<DdlStatement>();
            string tableRef = TableRef(t);

            var cols = new List<ColumnDef>();
            foreach (ColumnDef c in t.Columns)
            {
                if (string.IsNullOrEmpty(c.TargetType))
                {
                    if (_opt.SkipUnsupportedColumns)
                    {
                        _warnings.Add("表 " + t.QualifiedName + " 列 " + c.Name + " 无可用目标类型，已跳过该列");
                        continue;
                    }
                    throw new InvalidOperationException("表 " + t.QualifiedName + " 列 " + c.Name + " 没有目标类型，无法建表");
                }
                cols.Add(c);
            }
            if (cols.Count == 0)
            {
                throw new InvalidOperationException("表 " + t.QualifiedName + " 没有可迁移的列");
            }

            CheckNameLengths(t, cols);

            if (includeDrop && _opt.DropExisting)
            {
                list.Add(new DdlStatement
                {
                    Kind = "drop-table",
                    Table = t.QualifiedName,
                    Sql = "DROP TABLE " + tableRef + ";",
                    Critical = false
                });
            }

            if (_opt.CreateTable)
            {
                list.Add(new DdlStatement
                {
                    Kind = "create-table",
                    Table = t.QualifiedName,
                    Sql = BuildCreateTable(t, cols),
                    Critical = true
                });
            }

            if (!_opt.CreateTable)
            {
                // 目标表已存在，只做注释/索引；直接返回
                list.AddRange(BuildComments(t, cols));
                if (_opt.CreateIndexes && !_opt.CreateIndexesAfterLoad) list.AddRange(BuildIndexes(t, cols));
                return list;
            }

            // Oracle 11g 风格的序列 + 触发器（可选）
            if (_d.Family == DialectFamily.Oracle && string.Equals(_opt.IdentityMode, "sequence", StringComparison.OrdinalIgnoreCase))
            {
                foreach (ColumnDef c in cols)
                {
                    if (!c.IsIdentity) continue;
                    string seq = ClampName("SEQ_" + t.TargetName, 30);
                    string trg = ClampName("TRG_" + t.TargetName, 30);
                    list.Add(new DdlStatement
                    {
                        Kind = "sequence",
                        Table = t.QualifiedName,
                        Critical = true,
                        Sql = "CREATE SEQUENCE " + _d.Qualify(t.TargetSchema, seq) + " START WITH 1 INCREMENT BY 1;"
                    });
                    list.Add(new DdlStatement
                    {
                        Kind = "trigger",
                        Table = t.QualifiedName,
                        Critical = true,
                        Sql = "CREATE OR REPLACE TRIGGER " + _d.Qualify(t.TargetSchema, trg) + " BEFORE INSERT ON " + tableRef +
                              " FOR EACH ROW AS\nBEGIN\n  IF :NEW." + _d.Quote(c.TargetName) + " IS NULL THEN\n    SELECT " +
                              _d.Qualify(t.TargetSchema, seq) + ".NEXTVAL INTO :NEW." + _d.Quote(c.TargetName) + " FROM DUAL;\n  END IF;\nEND;"
                    });
                }
            }

            list.AddRange(BuildComments(t, cols));

            if (_opt.CreateIndexes && !_opt.CreateIndexesAfterLoad)
            {
                list.AddRange(BuildIndexes(t, cols));
            }

            return list;
        }

        public string BuildCreateTable(TableDef t, List<ColumnDef> cols)
        {
            var sb = new StringBuilder();
            sb.Append("CREATE TABLE ");
            if (_opt.CreateIfNotExists && _d.SupportsIfNotExists) sb.Append("IF NOT EXISTS ");
            sb.Append(TableRef(t)).Append(" (\n");

            var lines = new List<string>();
            foreach (ColumnDef c in cols)
            {
                lines.Add("  " + BuildColumnLine(t, c));
            }

            IndexDef pk = t.PrimaryKey;
            bool pkInline = false;

            if (_d.Family == DialectFamily.Sqlite)
            {
                // SQLite 的自增必须内联为 INTEGER PRIMARY KEY AUTOINCREMENT
                foreach (ColumnDef c in cols)
                {
                    if (c.IsIdentity && pk != null && pk.Columns.Count == 1 &&
                        string.Equals(pk.Columns[0].Name, c.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        pkInline = true;
                    }
                }
            }

            if (pk != null && pk.Columns.Count > 0 && !pkInline)
            {
                var parts = new List<string>();
                foreach (IndexColumn ic in pk.Columns)
                {
                    ColumnDef c = FindBySourceName(cols, ic.Name);
                    parts.Add(_d.Quote(c != null ? c.TargetName : ic.Name));
                }
                string name = ClampName(BuildConstraintName(pk.Name, t.TargetName + "_PK"), 30);
                if (_d.Family == DialectFamily.SqlServer)
                {
                    lines.Add("  CONSTRAINT " + _d.Quote(name) + " PRIMARY KEY (" + string.Join(", ", parts) + ")");
                }
                else
                {
                    lines.Add("  CONSTRAINT " + _d.Quote(name) + " PRIMARY KEY (" + string.Join(", ", parts) + ")");
                }
            }

            if (_opt.IncludeChecks && t.Checks.Count > 0)
            {
                foreach (CheckDef chk in t.Checks)
                {
                    string expr = RewriteExpr(chk.Expression, "CHECK", t.QualifiedName + "." + chk.Name);
                    if (string.IsNullOrWhiteSpace(expr)) continue;
                    string name = ClampName(BuildConstraintName(chk.Name, "CK_" + t.TargetName), 30);
                    lines.Add("  CONSTRAINT " + _d.Quote(name) + " CHECK (" + expr + ")");
                }
            }

            // SQLite 不支持 ALTER TABLE ADD FOREIGN KEY，只能在建表时内联
            if (_d.Family == DialectFamily.Sqlite && _opt.IncludeForeignKeys && t.ForeignKeys.Count > 0)
            {
                foreach (ForeignKeyDef fk in t.ForeignKeys)
                {
                    if (fk.Columns.Count != fk.RefColumns.Count || string.IsNullOrEmpty(fk.RefTable)) continue;
                    var local = new List<string>();
                    var remote = new List<string>();
                    bool ok = true;
                    foreach (string cn in fk.Columns)
                    {
                        ColumnDef c = FindBySourceName(cols, cn);
                        if (c == null) { ok = false; break; }
                        local.Add(_d.Quote(c.TargetName));
                    }
                    foreach (string cn in fk.RefColumns)
                    {
                        string mappedCol = _names == null ? null : _names.ResolveColumn(cn);
                        remote.Add(_d.Quote(mappedCol ?? SqlUtil.ApplyCase(cn, SqlUtil.ResolveCase(_opt.IdentifierCase, _d))));
                    }
                    if (!ok) continue;
                    string mappedRef = Names.Resolve(fk.RefSchema, fk.RefTable);
                    string refTableName = fk.RefTable;
                    if (mappedRef != null)
                    {
                        string[] parts = mappedRef.Split('.');
                        refTableName = parts[parts.Length - 1];
                    }
                    else
                    {
                        refTableName = SqlUtil.ApplyCase(fk.RefTable, SqlUtil.ResolveCase(_opt.IdentifierCase, _d));
                    }
                    lines.Add("  FOREIGN KEY (" + string.Join(", ", local) + ") REFERENCES " + _d.Quote(refTableName) +
                              " (" + string.Join(", ", remote) + ")");
                }
            }

            sb.Append(string.Join(",\n", lines));
            sb.Append("\n)");

            if (_d.Family == DialectFamily.MySql && !string.IsNullOrEmpty(t.Comment))
            {
                sb.Append(" COMMENT = ").Append(SqlUtil.Literal(t.Comment));
            }
            sb.Append(";");
            return sb.ToString();
        }

        private string BuildColumnLine(TableDef t, ColumnDef c)
        {
            var sb = new StringBuilder();
            sb.Append(_d.Quote(c.TargetName)).Append(' ').Append(c.TargetType);

            if (_d.Family == DialectFamily.Sqlite && c.IsIdentity &&
                FindPrimarySingle(t, c) != null)
            {
                // SQLite 要求严格写成 INTEGER PRIMARY KEY AUTOINCREMENT，后面不能再跟 NOT NULL
                sb.Append(" PRIMARY KEY AUTOINCREMENT");
                return sb.ToString();
            }
            else if (c.IsIdentity)
            {
                sb.Append(_d.IdentityClause(c, _opt));
            }

            if (c.IsComputed)
            {
                string gen = RewriteExpr(c.GenerationExpression, "列", t.QualifiedName + "." + c.Name);
                if (string.IsNullOrWhiteSpace(gen))
                {
                    _issues?.Add("列", t.QualifiedName + "." + c.Name, IssueLevel.Manual,
                        "源库是计算列，但没读到生成表达式，已按普通列建（数据需自行补齐）",
                        c.RawType, c.TargetType, "在目标库手工补 AS (表达式)");
                }
                else
                {
                    switch (_d.Family)
                    {
                        case DialectFamily.MySql:
                            sb.Append(" GENERATED ALWAYS AS (").Append(gen).Append(") ").Append(c.GeneratedStored ? "STORED" : "VIRTUAL");
                            break;
                        case DialectFamily.Postgres:
                            if (!c.GeneratedStored)
                            {
                                _issues?.Add("列", t.QualifiedName + "." + c.Name, IssueLevel.Manual,
                                    "PostgreSQL 只支持 STORED 生成列，已按 STORED 建（虚拟列需改为视图/表达式）",
                                    gen, gen, "确认存储开销可接受");
                            }
                            sb.Append(" GENERATED ALWAYS AS (").Append(gen).Append(") STORED");
                            break;
                        case DialectFamily.Sqlite:
                            sb.Append(" GENERATED ALWAYS AS (").Append(gen).Append(") ").Append(c.GeneratedStored ? "STORED" : "VIRTUAL");
                            break;
                        case DialectFamily.SqlServer:
                            sb.Append(" AS (").Append(gen).Append(")").Append(c.GeneratedStored ? " PERSISTED" : "");
                            break;
                        default:
                            sb.Append(" GENERATED ALWAYS AS (").Append(gen).Append(") VIRTUAL");
                            _issues?.Add("列", t.QualifiedName + "." + c.Name, IssueLevel.Manual,
                                "目标库的虚拟列语法需现场确认（达梦/金仓 版本差异较大）", gen, gen, "建表后在目标库验证一次");
                            break;
                    }
                    if (_d.Family == DialectFamily.MySql && !string.IsNullOrEmpty(c.Comment))
                    {
                        sb.Append(" COMMENT ").Append(SqlUtil.Literal(c.Comment));
                    }
                    return sb.ToString();
                }
            }

            if (!c.Nullable) sb.Append(" NOT NULL");

            if (_opt.IncludeDefaults && c.HasDefault && !c.IsIdentity && !string.IsNullOrWhiteSpace(c.DefaultExpression))
            {
                string def = RewriteExpr(c.DefaultExpression, "列默认值", t.QualifiedName + "." + c.Name);
                if (!string.IsNullOrWhiteSpace(def))
                {
                    sb.Append(" DEFAULT ").Append(def);
                    if (_d.Family == DialectFamily.Oracle && !IsLiteralDefault(def))
                    {
                        _issues?.Add("列默认值", t.QualifiedName + "." + c.Name, IssueLevel.Manual,
                            "该默认值是表达式，目标库可能不接受，需要现场验证", def, def,
                            "改成常量或建表后单独 ALTER");
                    }
                }
            }

            if (_d.Family == DialectFamily.MySql && !string.IsNullOrEmpty(c.Comment))
            {
                sb.Append(" COMMENT ").Append(SqlUtil.Literal(c.Comment));
            }
            return sb.ToString();
        }

        private IndexDef FindPrimarySingle(TableDef t, ColumnDef c)
        {
            IndexDef pk = t.PrimaryKey;
            if (pk == null || pk.Columns.Count != 1) return null;
            if (!string.Equals(pk.Columns[0].Name, c.Name, StringComparison.OrdinalIgnoreCase)) return null;
            return pk;
        }

        /// <summary>字面量默认值（字符串/数字/NULL/布尔/当前时间）目标库都能直接接受。</summary>
        private static bool IsLiteralDefault(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            string s = v.Trim();
            if (s.StartsWith("'")) return true;
            if (s.StartsWith("-")) s = s.Substring(1).TrimStart();
            bool numeric = s.Length > 0;
            bool dot = false;
            foreach (char ch in s)
            {
                if (ch >= '0' && ch <= '9') continue;
                if (ch == '.' && !dot) { dot = true; continue; }
                numeric = false;
                break;
            }
            if (numeric) return true;
            string u = v.Trim().ToUpperInvariant();
            if (u == "NULL" || u == "TRUE" || u == "FALSE") return true;
            if (u.IndexOf("SYSDATE", StringComparison.Ordinal) >= 0 ||
                u.IndexOf("SYSTIMESTAMP", StringComparison.Ordinal) >= 0 ||
                u.IndexOf("CURRENT_", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private ColumnDef FindBySourceName(List<ColumnDef> cols, string sourceName)
        {
            foreach (ColumnDef c in cols)
            {
                if (string.Equals(c.Name, sourceName, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        public List<DdlStatement> BuildComments(TableDef t, List<ColumnDef> cols)
        {
            var list = new List<DdlStatement>();
            if (_d.Family == DialectFamily.MySql || _d.Family == DialectFamily.Sqlite) return list;

            string tableRef = TableRef(t);

            if (_d.Family == DialectFamily.SqlServer)
            {
                if (!string.IsNullOrEmpty(t.Comment))
                {
                    list.Add(new DdlStatement
                    {
                        Kind = "comment",
                        Table = t.QualifiedName,
                        Sql = "EXEC sp_addextendedproperty @name=N'MS_Description', @value=" + NString(t.Comment) +
                              ", @level0type=N'SCHEMA', @level0name=" + NString(t.TargetSchema) +
                              ", @level1type=N'TABLE', @level1name=" + NString(t.TargetName) + ";"
                    });
                }
                foreach (ColumnDef c in cols)
                {
                    if (string.IsNullOrEmpty(c.Comment)) continue;
                    list.Add(new DdlStatement
                    {
                        Kind = "comment",
                        Table = t.QualifiedName,
                        Sql = "EXEC sp_addextendedproperty @name=N'MS_Description', @value=" + NString(c.Comment) +
                              ", @level0type=N'SCHEMA', @level0name=" + NString(t.TargetSchema) +
                              ", @level1type=N'TABLE', @level1name=" + NString(t.TargetName) +
                              ", @level2type=N'COLUMN', @level2name=" + NString(c.TargetName) + ";"
                    });
                }
                return list;
            }

            if (!_d.SupportsCommentOn) return list;

            if (!string.IsNullOrEmpty(t.Comment))
            {
                list.Add(new DdlStatement
                {
                    Kind = "comment",
                    Table = t.QualifiedName,
                    Sql = "COMMENT ON TABLE " + tableRef + " IS " + SqlUtil.Literal(t.Comment) + ";"
                });
            }
            foreach (ColumnDef c in cols)
            {
                if (string.IsNullOrEmpty(c.Comment)) continue;
                list.Add(new DdlStatement
                {
                    Kind = "comment",
                    Table = t.QualifiedName,
                    Sql = "COMMENT ON COLUMN " + tableRef + "." + _d.Quote(c.TargetName) + " IS " +
                          SqlUtil.Literal(c.Comment) + ";"
                });
            }
            return list;
        }

        private string NString(string s)
        {
            return "N'" + SqlUtil.EscapeString(s ?? "").Replace("\r", " ").Replace("\n", " ") + "'";
        }

        // ------------------------------------------------------------------
        // 外键 / 视图 / 过程草稿
        // ------------------------------------------------------------------
        public List<DdlStatement> BuildForeignKeys(TableDef t)
        {
            var list = new List<DdlStatement>();
            if (!_opt.IncludeForeignKeys || t.ForeignKeys.Count == 0) return list;

            if (_d.Family == DialectFamily.Sqlite)
            {
                foreach (ForeignKeyDef fk in t.ForeignKeys)
                {
                    _issues?.Add("外键", t.QualifiedName + "." + fk.Name, IssueLevel.Skipped,
                        "SQLite 不支持 ALTER TABLE ADD FOREIGN KEY，只能在建表语句内定义；当前建表已尝试内联",
                        fk.ToString(), "", "如需完整外键请改用支持外键的目标库");
                }
                return list;
            }

            foreach (ForeignKeyDef fk in t.ForeignKeys)
            {
                string obj = t.QualifiedName + "." + fk.Name;
                if (string.IsNullOrEmpty(fk.RefTable) || fk.Columns.Count == 0)
                {
                    _issues?.Add("外键", obj, IssueLevel.Blocking, "外键信息不完整，未生成", fk.ToString(), "", "人工确认列与引用表后补建");
                    continue;
                }
                if (fk.Columns.Count != fk.RefColumns.Count)
                {
                    _issues?.Add("外键", obj, IssueLevel.Blocking, "外键列数与引用列数不一致，未生成", fk.ToString(), "", "人工确认复合外键的列顺序");
                    continue;
                }

                var local = new List<string>();
                var remote = new List<string>();
                bool ok = true;
                foreach (string cn in fk.Columns)
                {
                    ColumnDef c = FindBySourceName(t.Columns, cn);
                    if (c == null || string.IsNullOrEmpty(c.TargetName)) { ok = false; break; }
                    local.Add(_d.Quote(c.TargetName));
                }
                foreach (string cn in fk.RefColumns)
                {
                    string mappedCol = _names == null ? null : _names.ResolveColumn(cn);
                    remote.Add(_d.Quote(mappedCol ?? SqlUtil.ApplyCase(cn, SqlUtil.ResolveCase(_opt.IdentifierCase, _d))));
                }
                if (!ok)
                {
                    _issues?.Add("外键", obj, IssueLevel.Skipped, "外键引用的列没有迁移，未生成", fk.ToString(), "", "确认列是否被跳过");
                    continue;
                }

                string refName = Names.Resolve(fk.RefSchema, fk.RefTable);
                string refSql = refName != null ? QuoteDotted(refName)
                    : _d.Qualify(t.TargetSchema, SqlUtil.ApplyCase(fk.RefTable, SqlUtil.ResolveCase(_opt.IdentifierCase, _d)));

                var sql = new StringBuilder();
                sql.Append("ALTER TABLE ").Append(TableRef(t))
                   .Append(" ADD CONSTRAINT ").Append(_d.Quote(ClampName(BuildConstraintName(fk.Name, "FK_" + t.TargetName), 30)))
                   .Append(" FOREIGN KEY (").Append(string.Join(", ", local)).Append(")")
                   .Append(" REFERENCES ").Append(refSql).Append(" (").Append(string.Join(", ", remote)).Append(")");

                string del = MapFkAction(fk.OnDelete, obj, "ON DELETE");
                if (!string.IsNullOrEmpty(del)) sql.Append(" ON DELETE ").Append(del);
                if (_d.Family == DialectFamily.Oracle)
                {
                    if (!string.IsNullOrWhiteSpace(fk.OnUpdate) && fk.OnUpdate.IndexOf("CASCADE", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _issues?.Add("外键", obj, IssueLevel.Manual, "目标库不支持 ON UPDATE 级联，已忽略",
                            fk.OnUpdate, "", "由业务代码或触发器维护");
                    }
                }
                else
                {
                    string upd = MapFkAction(fk.OnUpdate, obj, "ON UPDATE");
                    if (!string.IsNullOrEmpty(upd)) sql.Append(" ON UPDATE ").Append(upd);
                }
                sql.Append(";");
                list.Add(new DdlStatement { Kind = "foreign-key", Table = t.QualifiedName, Sql = sql.ToString() });
            }
            return list;
        }

        private string MapFkAction(string raw, string obj, string clause)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string u = raw.Trim().ToUpperInvariant();
            if (u == "NO ACTION" || u == "RESTRICT") return "NO ACTION";
            if (u == "CASCADE") return "CASCADE";
            if (u == "SET NULL") return "SET NULL";
            if (u == "SET DEFAULT")
            {
                _issues?.Add("外键", obj, IssueLevel.Manual, clause + " SET DEFAULT 在多数国产库不支持，已忽略", raw, "", "人工确认默认值策略");
                return "";
            }
            _issues?.Add("外键", obj, IssueLevel.Manual, "未识别的外键动作，已忽略", clause + " " + raw, "", "人工确认");
            return "";
        }

        private string QuoteDotted(string dotted)
        {
            string[] parts = dotted.Split('.');
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append('.');
                sb.Append(_d.Quote(parts[i]));
            }
            return sb.ToString();
        }

        public DdlStatement BuildView(ViewDef v, string targetSchema, string targetName)
        {
            string body = StripCreateView(v.Definition);
            string rewritten = RewriteExpr(body, "视图", v.QualifiedName());
            string refSql = _d.Qualify(targetSchema, targetName);
            string head;
            switch (_d.Family)
            {
                case DialectFamily.Sqlite: head = "CREATE VIEW IF NOT EXISTS " + refSql + " AS"; break;
                case DialectFamily.SqlServer: head = "CREATE OR ALTER VIEW " + refSql + " AS"; break;
                default: head = "CREATE OR REPLACE VIEW " + refSql + " AS"; break;
            }
            string sql = head + "\n" + rewritten.Trim().TrimEnd(';') + ";";
            return new DdlStatement { Kind = "view", Table = v.QualifiedName(), Sql = sql };
        }

        /// <summary>SQL Server 的 definition 是完整 CREATE VIEW，这里只留 SELECT 主体。</summary>
        private static string StripCreateView(string definition)
        {
            string s = (definition ?? "").Trim();
            if (s.Length == 0) return s;
            string upper = s.ToUpperInvariant();
            if (!upper.StartsWith("CREATE")) return s.TrimEnd(';');
            int asIdx = s.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
            if (asIdx < 0) return s;
            return s.Substring(asIdx + 4).Trim().TrimEnd(';');
        }

        /// <summary>过程/函数：生成“草稿”而不是可执行 DDL，所有不确定点都登记到问题清单。</summary>
        public string BuildRoutineDraft(RoutineDef r, bool reportIssue = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- ------------------------------------------------------------");
            sb.AppendLine("-- [需人工] " + (r.Kind == "function" ? "函数" : "存储过程") + "：" + r.Name);
            sb.AppendLine("-- 源库原文已保留；表名/函数名做过映射，DECLARE、游标、异常处理等语法需按目标库改写");
            sb.AppendLine("-- 目标方言：" + _d.Display);
            sb.AppendLine("-- ------------------------------------------------------------");
            string def = r.Definition ?? "";
            string rewritten = RewriteExpr(def, "过程", r.Name);
            foreach (string line in rewritten.Replace("\r", "").Split('\n'))
            {
                sb.AppendLine("-- " + line);
            }
            sb.AppendLine();
            if (reportIssue)
            {
                _issues?.Add(r.Kind == "function" ? "函数" : "存储过程", r.Name, IssueLevel.Manual,
                    "已抽取源码并生成改造草稿，未自动执行", "", "", "按草稿在目标库编译，逐条处理 TODO");
            }
            return sb.ToString();
        }

        public string BuildTriggerDraft(TriggerDef tr, bool reportIssue = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- ------------------------------------------------------------");
            sb.AppendLine("-- [需人工] 触发器：" + tr.Name + "（表 " + tr.Table + "，" + tr.Timing + " " + tr.Event + "）");
            sb.AppendLine("-- 触发器会改变业务数据，默认不在搬数期间创建；请评审后再手工执行");
            sb.AppendLine("-- ------------------------------------------------------------");
            string def = tr.Definition ?? "";
            string rewritten = RewriteExpr(def, "触发器", tr.Name);
            foreach (string line in rewritten.Replace("\r", "").Split('\n'))
            {
                sb.AppendLine("-- " + line);
            }
            sb.AppendLine();
            if (reportIssue)
            {
                _issues?.Add("触发器", tr.Name, IssueLevel.Manual,
                    "已抽取源码并生成改造草稿，未自动执行（避免搬数时触发业务规则）", "", "",
                    "数据搬完后评审创建；注意 NEW/OLD 语法差异");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 过程/函数能否直接执行：只有拿到完整 CREATE 且源/目标属于同一语法家族时才尝试。
        /// 返回 null 时 reason 说明为什么不直连，调用方仍会生成草稿并登记问题。
        /// </summary>
        public string BuildRoutineExecutable(RoutineDef r, out string reason)
        {
            if (!CanExecuteRoutine(r, out reason)) return null;
            string def = (r.Definition ?? "").Trim();
            def = StripDefiner(def);
            def = RewriteExpr(def, "过程", r.Name);
            return def;
        }

        public bool CanExecuteRoutine(RoutineDef r, out string reason)
        {
            reason = null;
            if (_src == null || _d == null)
            {
                reason = "缺少源/目标方言信息";
                return false;
            }
            if (_src.Family != _d.Family)
            {
                reason = "源库是 " + _src.Display + "，目标库是 " + _d.Display + "，过程/函数语法不能安全直译";
                return false;
            }
            if (!LooksLikeCreate(r.Definition))
            {
                reason = "没有取得完整 CREATE 语句（可能权限不足或源库只提供正文）";
                return false;
            }
            return true;
        }

        public string BuildTriggerExecutable(TriggerDef tr, out string reason)
        {
            if (!CanExecuteTrigger(tr, out reason)) return null;
            string def = (tr.Definition ?? "").Trim();
            def = StripDefiner(def);
            def = RewriteExpr(def, "触发器", tr.Name);
            return def;
        }

        public bool CanExecuteTrigger(TriggerDef tr, out string reason)
        {
            reason = null;
            if (_src == null || _d == null)
            {
                reason = "缺少源/目标方言信息";
                return false;
            }
            if (_src.Family != _d.Family)
            {
                reason = "源库是 " + _src.Display + "，目标库是 " + _d.Display + "，触发器语法不能安全直译";
                return false;
            }
            if (!LooksLikeCreate(tr.Definition))
            {
                reason = "没有取得完整 CREATE TRIGGER 语句";
                return false;
            }
            return true;
        }

        /// <summary>重建同名对象前先删除；PG 的过程/函数用 CREATE OR REPLACE，不需要 DROP。</summary>
        public string BuildRoutineDrop(RoutineDef r, string targetSchema)
        {
            if (_d == null) return null;
            string keyword = r.Kind == "function" ? "FUNCTION" : "PROCEDURE";
            if (_d.Family == DialectFamily.MySql || _d.Family == DialectFamily.SqlServer)
                return "DROP " + keyword + " IF EXISTS " + _d.Qualify(targetSchema, r.Name);
            return null;
        }

        public string BuildTriggerDrop(TriggerDef tr, string targetSchema, string targetTable)
        {
            if (_d == null) return null;
            string trig = _d.Qualify(targetSchema, tr.Name);
            if (_d.Family == DialectFamily.Postgres)
                return "DROP TRIGGER IF EXISTS " + _d.Quote(tr.Name) + " ON " + _d.Qualify(targetSchema, targetTable);
            if (_d.Family == DialectFamily.MySql || _d.Family == DialectFamily.SqlServer ||
                _d.Family == DialectFamily.Sqlite)
                return "DROP TRIGGER IF EXISTS " + trig;
            return null;
        }

        private static bool LooksLikeCreate(string sql)
        {
            string s = (sql ?? "").TrimStart();
            string upper = s.ToUpperInvariant();
            return upper.StartsWith("CREATE ") || upper.StartsWith("CREATE OR REPLACE ") ||
                   upper.StartsWith("CREATE OR ALTER ");
        }

        /// <summary>MySQL 的 SHOW CREATE 会带 DEFINER=`user`@`host`，换库/换账号后通常无权使用，直接去掉。</summary>
        private static readonly Regex DefinerRx = new Regex(
            @"\s+DEFINER\s*=\s*(?:`[^`]*`|'[^']*'|[^\s@]+)(?:\s*@\s*(?:`[^`]*`|'[^']*'|[^\s]+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string StripDefiner(string sql)
        {
            return string.IsNullOrEmpty(sql) ? sql : DefinerRx.Replace(sql, "");
        }

        public List<DdlStatement> BuildIndexes(TableDef t, List<ColumnDef> cols)
        {
            var list = new List<DdlStatement>();
            IndexDef pk = t.PrimaryKey;

            foreach (IndexDef ix in t.Indexes)
            {
                if (ix.IsPrimary) continue;
                if (ix.Columns.Count == 0) continue;
                if (pk != null && SameColumns(ix, pk)) continue;

                var parts = new List<string>();
                bool ok = true;
                foreach (IndexColumn ic in ix.Columns)
                {
                    ColumnDef c = FindBySourceName(cols, ic.Name);
                    if (c == null)
                    {
                        ok = false;
                        break;
                    }
                    parts.Add(_d.Quote(c.TargetName) + (ic.Descending ? " DESC" : ""));
                }
                if (!ok)
                {
                    _warnings.Add("表 " + t.QualifiedName + " 索引 " + ix.Name + " 有列未迁移，已跳过该索引");
                    continue;
                }

                string name = ClampName(BuildConstraintName(ix.Name, "IX_" + t.TargetName), 30);
                string indexRef = (_d.Family == DialectFamily.Postgres || _d.Family == DialectFamily.Oracle)
                    ? _d.Qualify(t.TargetSchema, name)
                    : _d.Quote(name);
                // SQLite 的索引语法不允许在 ON 后面写 schema
                string onRef = _d.Family == DialectFamily.Sqlite ? _d.Quote(t.TargetName) : TableRef(t);
                string sql = "CREATE " + (ix.IsUnique ? "UNIQUE " : "") + "INDEX " + indexRef +
                             " ON " + onRef + " (" + string.Join(", ", parts) + ");";
                list.Add(new DdlStatement { Kind = "index", Table = t.QualifiedName, Sql = sql });
            }
            return list;
        }

        private static bool SameColumns(IndexDef a, IndexDef b)
        {
            if (a.Columns.Count != b.Columns.Count) return false;
            foreach (IndexColumn c in a.Columns)
            {
                bool found = false;
                foreach (IndexColumn d in b.Columns)
                {
                    if (string.Equals(c.Name, d.Name, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                }
                if (!found) return false;
            }
            return true;
        }

        private string BuildConstraintName(string sourceName, string fallback)
        {
            string name = sourceName;
            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, "PRIMARY", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("sqlite_autoindex", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "PK", StringComparison.OrdinalIgnoreCase))
            {
                name = fallback;
            }
            return SqlUtil.ApplyCase(name, SqlUtil.ResolveCase(_opt.IdentifierCase, _d));
        }

        private void CheckNameLengths(TableDef t, List<ColumnDef> cols)
        {
            int limit = _d.Family == DialectFamily.Oracle ? 30 : (_d.Family == DialectFamily.MySql ? 64 : 63);
            if (t.TargetName.Length > limit)
            {
                _warnings.Add("表名 " + t.TargetName + " 超过 " + _d.Display + " 标识符长度上限 " + limit + "，建表可能失败");
            }
            foreach (ColumnDef c in cols)
            {
                if (c.TargetName.Length > limit)
                {
                    _warnings.Add("表 " + t.TargetName + " 列名 " + c.TargetName + " 超过长度上限 " + limit + "，建表可能失败");
                }
            }

            if (_d.Family == DialectFamily.MySql)
            {
                long total = 0;
                int identityCount = 0;
                foreach (ColumnDef c in cols)
                {
                    if (c.IsIdentity) identityCount++;
                    string tt = c.TargetType ?? "";
                    if (tt.StartsWith("VARCHAR(") || tt.StartsWith("CHAR(") || tt.StartsWith("VARBINARY("))
                    {
                        string num = tt.Substring(tt.IndexOf('(') + 1).TrimEnd(')');
                        int n = 0;
                        if (int.TryParse(num, out n)) total += n * (tt.StartsWith("VARBINARY") ? 1 : 4);
                    }
                }
                if (total > 60000)
                {
                    _warnings.Add("表 " + t.TargetName + " 字符列总字节数约 " + total + "，接近 MySQL 单行 65535 上限，建议把部分列改为 TEXT");
                }
                if (identityCount > 1)
                {
                    _warnings.Add("表 " + t.TargetName + " 有多个自增列，MySQL 只允许一个 AUTO_INCREMENT");
                }
            }
        }

        public static string ClampName(string name, int limit)
        {
            if (string.IsNullOrEmpty(name) || name.Length <= limit) return name;
            string hash = BitConverter.ToString(Encoding.UTF8.GetBytes(name)).Replace("-", "").Substring(0, 6);
            return name.Substring(0, limit - 7) + "_" + hash;
        }
    }
}
