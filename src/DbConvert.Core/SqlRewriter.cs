using System;
using System.Collections.Generic;
using System.Text;

namespace DbConvert
{
    /// <summary>视图/过程里出现的表名 → 目标表名映射（只作用于标识符，不碰字符串）。</summary>
    public class NameMap
    {
        private readonly Dictionary<string, string> _qualified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _bare = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _bareSchema = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void Add(string schema, string name, string targetSchema, string targetName)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(targetName)) return;
            if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(schema ?? "", targetSchema ?? "", StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrEmpty(schema)) _qualified[schema + "." + name] = (string.IsNullOrEmpty(targetSchema) ? "" : targetSchema + ".") + targetName;
            _bare[name] = targetName;
            if (!string.IsNullOrEmpty(targetSchema)) _bareSchema[name] = targetSchema;
        }

        /// <summary>列名映射（表达式、CHECK、生成列里必须用目标库的列名大小写）。</summary>
        public void AddColumn(string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return;
            if (string.Equals(source, target, StringComparison.Ordinal)) return; // 仅大小写不同也要记录
            _columns[source] = target;
        }

        public string ResolveColumn(string name)
        {
            return _columns.TryGetValue(name, out string t) ? t : null;
        }

        /// <summary>返回 null 表示这个名字不需要改写；否则返回 "schema.table" 或 "table"。</summary>
        public string Resolve(string schema, string name)
        {
            if (!string.IsNullOrEmpty(schema))
            {
                string key = schema + "." + name;
                if (_qualified.TryGetValue(key, out string full)) return full;
            }
            if (_bare.TryGetValue(name, out string target))
            {
                string ts = _bareSchema.TryGetValue(name, out string s) ? s : "";
                return string.IsNullOrEmpty(ts) ? target : ts + "." + target;
            }
            return null;
        }

        public bool Empty => _bare.Count == 0;
    }

    /// <summary>
    /// 轻量 SQL 改写器：按词法切分（区分字符串/注释/标识符），只改函数名和表名引用，
    /// 不确定的一律保留原文并登记到问题清单，绝不静默替换。
    /// </summary>
    public static class SqlRewriter
    {
        private enum TokKind { Word, Quoted, String, Comment, Number, Symbol }

        private class Tok
        {
            public TokKind Kind;
            public string Text = "";
            public string Body = ""; // Quoted 里的裸内容
        }

        public static string Rewrite(string sql, Dialect source, Dialect target, NameMap names,
                                     IssueLog issues, string objectType, string objectName)
        {
            if (string.IsNullOrWhiteSpace(sql)) return sql ?? "";
            List<Tok> toks = Tokenize(sql);
            var sb = new StringBuilder();
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < toks.Count; i++)
            {
                Tok t = toks[i];

                if (t.Kind == TokKind.Quoted)
                {
                    // "schema"."table" 形式
                    int dotIdx = NextNonSpace(toks, i + 1);
                    if (dotIdx >= 0 && toks[dotIdx].Kind == TokKind.Symbol && toks[dotIdx].Text == ".")
                    {
                        int nameIdx = NextNonSpace(toks, dotIdx + 1);
                        if (nameIdx >= 0 && (toks[nameIdx].Kind == TokKind.Word || toks[nameIdx].Kind == TokKind.Quoted))
                        {
                            string tableName = toks[nameIdx].Kind == TokKind.Quoted ? toks[nameIdx].Body : toks[nameIdx].Text;
                            string mappedQ = names == null ? null : names.Resolve(t.Body, tableName);
                            if (mappedQ != null)
                            {
                                AppendName(sb, mappedQ, target);
                                i = nameIdx;
                                continue;
                            }
                        }
                    }

                    string colTarget = names == null ? null : names.ResolveColumn(t.Body);
                    if (colTarget != null)
                    {
                        AppendName(sb, colTarget, target);
                        continue;
                    }

                    string mapped = names == null ? null : names.Resolve("", t.Body);
                    if (mapped != null)
                    {
                        AppendName(sb, mapped, target);
                    }
                    else
                    {
                        sb.Append(target.QuoteOpen).Append(t.Body.Replace(target.QuoteClose, target.QuoteClose + target.QuoteClose)).Append(target.QuoteClose);
                    }
                    continue;
                }

                if (t.Kind == TokKind.Word)
                {
                    int next = NextNonSpace(toks, i + 1);

                    // schema.table 形式
                    if (next >= 0 && toks[next].Kind == TokKind.Symbol && toks[next].Text == ".")
                    {
                        int nameIdx = NextNonSpace(toks, next + 1);
                        if (nameIdx >= 0 && (toks[nameIdx].Kind == TokKind.Word || toks[nameIdx].Kind == TokKind.Quoted))
                        {
                            string tableName = toks[nameIdx].Kind == TokKind.Quoted ? toks[nameIdx].Body : toks[nameIdx].Text;
                            string mapped = names == null ? null : names.Resolve(t.Text, tableName);
                            if (mapped != null)
                            {
                                AppendName(sb, mapped, target);
                                i = nameIdx;
                                continue;
                            }
                        }
                    }

                    // 函数调用
                    if (next >= 0 && toks[next].Kind == TokKind.Symbol && toks[next].Text == "(")
                    {
                        int close = MatchParen(toks, next);
                        if (close > 0)
                        {
                            string rewritten = TryRewriteFunction(t, toks, next, close, source, target, names, issues, objectType, objectName, reported);
                            if (rewritten != null)
                            {
                                sb.Append(rewritten);
                                i = close;
                                continue;
                            }
                        }
                    }

                    // 普通表名引用
                    string m2 = names == null ? null : names.Resolve("", t.Text);
                    if (m2 != null)
                    {
                        AppendName(sb, m2, target);
                        continue;
                    }

                    string colName = names == null ? null : names.ResolveColumn(t.Text);
                    if (colName != null)
                    {
                        AppendName(sb, colName, target);
                        continue;
                    }

                    if (string.Equals(t.Text, "LIMIT", StringComparison.OrdinalIgnoreCase) && target.Family == DialectFamily.Oracle)
                    {
                        ReportOnce(issues, reported, objectType, objectName, "LIMIT 分页",
                            "目标库不支持 LIMIT，需要改成 ROWNUM / FETCH FIRST 或在外层包一层",
                            "LIMIT", "LIMIT", "改写为 SELECT * FROM (… WHERE ROWNUM <= n) 后人工确认边界");
                    }
                    sb.Append(t.Text);
                    continue;
                }

                sb.Append(t.Text);
            }
            return sb.ToString();
        }

        private static void AppendName(StringBuilder sb, string dotted, Dialect target)
        {
            string[] parts = dotted.Split('.');
            for (int k = 0; k < parts.Length; k++)
            {
                if (k > 0) sb.Append('.');
                sb.Append(target.QuoteOpen).Append(parts[k]).Append(target.QuoteClose);
            }
        }

        // ------------------------------------------------------------------
        // 函数改写
        // ------------------------------------------------------------------
        private static string TryRewriteFunction(Tok nameTok, List<Tok> toks, int open, int close, Dialect source, Dialect target,
                                                 NameMap names, IssueLog issues, string objectType, string objectName, HashSet<string> reported)
        {
            string name = nameTok.Text;
            string upper = name.ToUpperInvariant();
            List<string> args = SplitArgs(toks, open, close);

            switch (upper)
            {
                case "NOW":
                case "SYSDATE":
                case "GETDATE":
                    if (target.Family == DialectFamily.Oracle)
                    {
                        ReportOnce(issues, reported, objectType, objectName, name + "()", "时间函数改写为 SYSDATE", name + "()", "SYSDATE", "");
                        return "SYSDATE";
                    }
                    if (target.Family == DialectFamily.MySql && source.Family != DialectFamily.MySql) return "NOW()";
                    if (target.Family == DialectFamily.SqlServer) return "GETDATE()";
                    return "CURRENT_TIMESTAMP";

                case "CURDATE":
                case "CURRENT_DATE":
                    if (target.Family == DialectFamily.Oracle)
                    {
                        ReportOnce(issues, reported, objectType, objectName, "CURDATE()", "日期函数改写为 TRUNC(SYSDATE)", "CURDATE()", "TRUNC(SYSDATE)", "确认是否只取日期部分");
                        return "TRUNC(SYSDATE)";
                    }
                    return "CURRENT_DATE";

                case "IFNULL":
                case "NVL":
                    if (target.Family == DialectFamily.Oracle)
                    {
                        ReportOnce(issues, reported, objectType, objectName, "IFNULL/NVL", "空值兜底函数已改写为 NVL", "IFNULL", "NVL", "");
                        return "NVL(" + RewriteArgs(args, source, target, names, issues, objectType, objectName) + ")";
                    }
                    if (target.Family == DialectFamily.SqlServer)
                    {
                        ReportOnce(issues, reported, objectType, objectName, "IFNULL", "改写为 ISNULL", "IFNULL", "ISNULL", "");
                        return "ISNULL(" + RewriteArgs(args, source, target, names, issues, objectType, objectName) + ")";
                    }
                    ReportOnce(issues, reported, objectType, objectName, "IFNULL", "改写为 COALESCE（参数个数不限，语义等价）", "IFNULL", "COALESCE", "");
                    return "COALESCE(" + RewriteArgs(args, source, target, names, issues, objectType, objectName) + ")";

                case "IF":
                    if (args.Count == 3 && target.Family != DialectFamily.MySql)
                    {
                        ReportOnce(issues, reported, objectType, objectName, "IF(cond,a,b)", "三目函数改写为 CASE WHEN", "IF(...)", "CASE WHEN ... END", "确认条件里的布尔表达式在目标库可直接使用");
                        return "CASE WHEN " + RewriteTerm(args[0], source, target, names, issues, objectType, objectName) +
                               " THEN " + RewriteTerm(args[1], source, target, names, issues, objectType, objectName) +
                               " ELSE " + RewriteTerm(args[2], source, target, names, issues, objectType, objectName) + " END";
                    }
                    break;

                case "DATE_FORMAT":
                    if (args.Count == 2)
                    {
                        string fmt = StripQuotes(args[1].Trim());
                        string converted = ConvertDateFormat(fmt);
                        if (target.Family == DialectFamily.Oracle)
                        {
                            ReportOnce(issues, reported, objectType, objectName, "DATE_FORMAT", "格式化串已转换为 Oracle 格式", fmt, converted, "重点核对 %W/%a 这类本地化格式");
                            return "TO_CHAR(" + RewriteTerm(args[0], source, target, names, issues, objectType, objectName) + ", '" + converted + "')";
                        }
                        if (target.Family == DialectFamily.Postgres)
                        {
                            ReportOnce(issues, reported, objectType, objectName, "DATE_FORMAT", "格式化串已转换为 PG 格式", fmt, converted, "");
                            return "TO_CHAR(" + RewriteTerm(args[0], source, target, names, issues, objectType, objectName) + ", '" + converted + "')";
                        }
                        if (target.Family == DialectFamily.Sqlite)
                        {
                            return "strftime('" + fmt + "', " + RewriteTerm(args[0], source, target, names, issues, objectType, objectName) + ")";
                        }
                        if (target.Family == DialectFamily.SqlServer)
                        {
                            ReportOnce(issues, reported, objectType, objectName, "DATE_FORMAT", "SQL Server 需改用 FORMAT/CONVERT", fmt, "", "建议人工改写为 FORMAT(date,'yyyy-MM-dd')");
                        }
                    }
                    break;

                case "STR_TO_DATE":
                    if (args.Count == 2)
                    {
                        string fmt2 = ConvertDateFormat(StripQuotes(args[1].Trim()));
                        if (target.Family == DialectFamily.Oracle || target.Family == DialectFamily.Postgres)
                        {
                            ReportOnce(issues, reported, objectType, objectName, "STR_TO_DATE", "改写为 TO_DATE", args.Count.ToString(), "'" + fmt2 + "'", "确认格式串与时区");
                            return "TO_DATE(" + RewriteTerm(args[0], source, target, names, issues, objectType, objectName) + ", '" + fmt2 + "')";
                        }
                    }
                    break;

                case "GROUP_CONCAT":
                    {
                        string expr = null;
                        string sep = "','";
                        foreach (string a in args)
                        {
                            string trim = a.Trim();
                            if (trim.StartsWith("SEPARATOR", StringComparison.OrdinalIgnoreCase))
                            {
                                sep = trim.Substring("SEPARATOR".Length).Trim();
                                if (string.IsNullOrEmpty(sep)) sep = "','";
                            }
                            else if (expr == null)
                            {
                                expr = trim;
                            }
                        }
                        if (expr == null) break;
                        string rx = RewriteTerm(expr, source, target, names, issues, objectType, objectName);
                        string rs = RewriteTerm(sep, source, target, names, issues, objectType, objectName);
                        if (target.Family == DialectFamily.Oracle)
                        {
                            ReportOnce(issues, reported, objectType, objectName, "GROUP_CONCAT", "改写为 LISTAGG（需确认排序与长度限制）", "GROUP_CONCAT(x SEPARATOR s)", "LISTAGG(x,s) WITHIN GROUP (ORDER BY x)", "LISTAGG 结果超 4000 字节会报错，必要时改 CLOB");
                            return "LISTAGG(" + rx + ", " + rs + ") WITHIN GROUP (ORDER BY " + rx + ")";
                        }
                        if (target.Family == DialectFamily.Postgres)
                        {
                            ReportOnce(issues, reported, objectType, objectName, "GROUP_CONCAT", "改写为 STRING_AGG", "GROUP_CONCAT", "STRING_AGG", "");
                            return "STRING_AGG(" + rx + ", " + rs + ")";
                        }
                        if (target.Family == DialectFamily.SqlServer)
                        {
                            ReportOnce(issues, reported, objectType, objectName, "GROUP_CONCAT", "改写为 STRING_AGG（SQL Server 2017+）", "GROUP_CONCAT", "STRING_AGG", "");
                            return "STRING_AGG(" + rx + ", " + rs + ")";
                        }
                        return "GROUP_CONCAT(" + rx + ", " + rs + ")";
                    }

                case "CONCAT":
                    if (target.Family == DialectFamily.Oracle || target.Family == DialectFamily.Sqlite)
                    {
                        var parts = new List<string>();
                        foreach (string a in args) parts.Add(RewriteTerm(a, source, target, names, issues, objectType, objectName));
                        ReportOnce(issues, reported, objectType, objectName, "CONCAT", "改写为 || 拼接", "CONCAT(...)", "a || b || c",
                                   target.Family == DialectFamily.Sqlite ? "SQLite 没有 CONCAT 函数，必须用 ||" : "任一参数为 NULL 时结果整体为 NULL，与 MySQL 行为不同");
                        return "(" + string.Join(" || ", parts) + ")";
                    }
                    break;

                case "CAST":
                case "CONVERT":
                    {
                        string inner = string.Join(",", args).Trim();
                        string typeText = null;
                        int asIdx = -1;
                        if (upper == "CAST")
                        {
                            asIdx = IndexOfTopLevelWord(inner, "AS");
                            if (asIdx >= 0) typeText = inner.Substring(asIdx + 2).Trim();
                        }
                        if (typeText != null)
                        {
                            string mappedType = MapCastType(typeText, target);
                            if (mappedType != null)
                            {
                                string val = inner.Substring(0, asIdx).Trim();
                                ReportOnce(issues, reported, objectType, objectName, "CAST 类型", "CAST 目标类型已映射", typeText, mappedType, "");
                                return "CAST(" + RewriteTerm(val, source, target, names, issues, objectType, objectName) + " AS " + mappedType + ")";
                            }
                        }
                    }
                    break;

                case "SUBSTRING":
                case "SUBSTR":
                    if (target.Family == DialectFamily.Oracle)
                        return "SUBSTR(" + RewriteArgs(args, source, target, names, issues, objectType, objectName) + ")";
                    if (target.Family == DialectFamily.Sqlite)
                        return "SUBSTR(" + RewriteArgs(args, source, target, names, issues, objectType, objectName) + ")";
                    return null;

                case "CHAR_LENGTH":
                case "CHARACTER_LENGTH":
                    if (target.Family != DialectFamily.MySql)
                    {
                        ReportOnce(issues, reported, objectType, objectName, "CHAR_LENGTH", "改写为 LENGTH（按字符计）", "CHAR_LENGTH", "LENGTH", "");
                        return "LENGTH(" + RewriteArgs(args, source, target, names, issues, objectType, objectName) + ")";
                    }
                    break;

                case "TRUNCATE":
                    if (target.Family != DialectFamily.MySql)
                    {
                        ReportOnce(issues, reported, objectType, objectName, "TRUNCATE(n,d)", "数值截断改写为 TRUNC", "TRUNCATE", "TRUNC", "");
                        return "TRUNC(" + RewriteArgs(args, source, target, names, issues, objectType, objectName) + ")";
                    }
                    break;

                case "UUID":
                    if (target.Family == DialectFamily.Oracle)
                    {
                        ReportOnce(issues, reported, objectType, objectName, "UUID()", "改写为 SYS_GUID()", "UUID()", "SYS_GUID()", "SYS_GUID 返回 RAW(16)，如列是 VARCHAR 需再 TO_CHAR");
                        return "SYS_GUID()";
                    }
                    break;
            }

            // 未识别但已知的 MySQL 专有函数：原样保留 + 提示
            if (source.Family == DialectFamily.MySql && target.Family != DialectFamily.MySql && KnownManual.Contains(upper))
            {
                ReportOnce(issues, reported, objectType, objectName, name + "()", "该函数在目标库没有直接对应，已原样保留，需要人工改写",
                           name + "(" + string.Join(",", args) + ")", name, "查目标库等价函数后替换");
            }
            return null;
        }

        private static readonly HashSet<string> KnownManual = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DATE_ADD","DATE_SUB","ADDDATE","SUBDATE","DATEDIFF","TIMEDIFF","TIMESTAMPDIFF","UNIX_TIMESTAMP","FROM_UNIXTIME",
            "LAST_DAY","DAYOFWEEK","DAYOFMONTH","DAYOFYEAR","WEEK","WEEKOFYEAR","MONTHNAME","DAYNAME","SEC_TO_TIME","TIME_TO_SEC",
            "FIND_IN_SET","LOCATE","INSTR","REGEXP","REGEXP_REPLACE","JSON_EXTRACT","JSON_UNQUOTE","JSON_ARRAY","JSON_OBJECT",
            "BINARY","CONVERT_TZ","GET_FORMAT","MAKEDATE","TO_DAYS","PERIOD_ADD","PERIOD_DIFF","ANY_VALUE","GROUP_CONCAT_MAX_LEN"
        };

        private static string MapCastType(string type, Dialect target)
        {
            string u = type.Trim().ToUpperInvariant();
            if (target.Family == DialectFamily.Oracle)
            {
                if (u == "SIGNED" || u == "SIGNED INTEGER") return "NUMBER(19)";
                if (u == "UNSIGNED" || u == "UNSIGNED INTEGER") return "NUMBER(20)";
                if (u == "CHAR" || u == "NCHAR") return "VARCHAR2(4000)";
                if (u == "DATETIME") return "TIMESTAMP";
            }
            if (target.Family == DialectFamily.Postgres)
            {
                if (u == "SIGNED" || u == "SIGNED INTEGER") return "BIGINT";
                if (u == "UNSIGNED" || u == "UNSIGNED INTEGER") return "NUMERIC(20)";
                if (u == "CHAR") return "VARCHAR(4000)";
                if (u == "DATETIME") return "TIMESTAMP";
            }
            if (target.Family == DialectFamily.Sqlite)
            {
                if (u == "SIGNED" || u == "UNSIGNED" || u.Contains("INT")) return "INTEGER";
                if (u == "CHAR") return "TEXT";
            }
            return null;
        }

        private static string ConvertDateFormat(string fmt)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < fmt.Length; i++)
            {
                char ch = fmt[i];
                if (ch != '%' || i + 1 >= fmt.Length)
                {
                    sb.Append(ch);
                    continue;
                }
                char n = fmt[++i];
                switch (n)
                {
                    case 'Y': sb.Append("YYYY"); break;
                    case 'y': sb.Append("YY"); break;
                    case 'm': sb.Append("MM"); break;
                    case 'c': sb.Append("FMMM"); break;
                    case 'd': sb.Append("DD"); break;
                    case 'e': sb.Append("FMDD"); break;
                    case 'H': sb.Append("HH24"); break;
                    case 'h':
                    case 'I': sb.Append("HH12"); break;
                    case 'i': sb.Append("MI"); break;
                    case 's':
                    case 'S': sb.Append("SS"); break;
                    case 'M': sb.Append("MONTH"); break;
                    case 'b': sb.Append("MON"); break;
                    case 'W': sb.Append("DAY"); break;
                    case 'a': sb.Append("DY"); break;
                    case 'p': sb.Append("AM"); break;
                    case 'T': sb.Append("HH24:MI:SS"); break;
                    case '%': sb.Append('%'); break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }

        private static string StripQuotes(string s)
        {
            if (s.Length >= 2 && ((s[0] == '\'' && s[s.Length - 1] == '\'') || (s[0] == '"' && s[s.Length - 1] == '"')))
                return s.Substring(1, s.Length - 2);
            return s;
        }

        private static int IndexOfTopLevelWord(string s, string word)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'') inStr = !inStr;
                if (inStr) continue;
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && i + word.Length <= s.Length &&
                         string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    bool leftOk = i == 0 || !char.IsLetterOrDigit(s[i - 1]);
                    bool rightOk = i + word.Length >= s.Length || !char.IsLetterOrDigit(s[i + word.Length]);
                    if (leftOk && rightOk) return i;
                }
            }
            return -1;
        }

        private static string RewriteArgs(List<string> args, Dialect source, Dialect target, NameMap names,
                                          IssueLog issues, string objectType, string objectName)
        {
            var parts = new List<string>();
            foreach (string a in args) parts.Add(RewriteTerm(a, source, target, names, issues, objectType, objectName));
            return string.Join(", ", parts);
        }

        private static string RewriteTerm(string sql, Dialect source, Dialect target, NameMap names,
                                          IssueLog issues, string objectType, string objectName)
        {
            return Rewrite(sql, source, target, names, issues, objectType, objectName);
        }

        private static void ReportOnce(IssueLog issues, HashSet<string> reported, string objectType, string objectName,
                                       string key, string message, string source, string result, string suggestion)
        {
            if (issues == null) return;
            if (!reported.Add(objectType + "|" + objectName + "|" + key)) return;
            issues.Add(objectType, objectName, IssueLevel.Rewritten, message, source, result, suggestion);
        }

        // ------------------------------------------------------------------
        // 词法
        // ------------------------------------------------------------------
        private static List<Tok> Tokenize(string sql)
        {
            var list = new List<Tok>();
            int i = 0;
            while (i < sql.Length)
            {
                char c = sql[i];

                if (char.IsWhiteSpace(c))
                {
                    int s = i;
                    while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
                    list.Add(new Tok { Kind = TokKind.Symbol, Text = sql.Substring(s, i - s) });
                    continue;
                }

                if (c == '\'')
                {
                    int s = i++;
                    while (i < sql.Length)
                    {
                        if (sql[i] == '\\' && i + 1 < sql.Length) { i += 2; continue; }
                        if (sql[i] == '\'')
                        {
                            if (i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                            i++;
                            break;
                        }
                        i++;
                    }
                    list.Add(new Tok { Kind = TokKind.String, Text = sql.Substring(s, i - s) });
                    continue;
                }

                if (c == '`' || c == '"' || c == '[')
                {
                    char end = c == '[' ? ']' : c;
                    int s = ++i;
                    while (i < sql.Length && sql[i] != end) i++;
                    string body = sql.Substring(s, i - s);
                    if (i < sql.Length) i++;
                    list.Add(new Tok { Kind = TokKind.Quoted, Text = sql.Substring(s - 1, i - (s - 1)), Body = body });
                    continue;
                }

                if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    int s = i;
                    while (i < sql.Length && sql[i] != '\n') i++;
                    list.Add(new Tok { Kind = TokKind.Comment, Text = sql.Substring(s, i - s) });
                    continue;
                }

                if (c == '#')
                {
                    int s = i;
                    while (i < sql.Length && sql[i] != '\n') i++;
                    list.Add(new Tok { Kind = TokKind.Comment, Text = sql.Substring(s, i - s) });
                    continue;
                }

                if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    int s = i;
                    i += 2;
                    while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                    i = Math.Min(sql.Length, i + 2);
                    list.Add(new Tok { Kind = TokKind.Comment, Text = sql.Substring(s, i - s) });
                    continue;
                }

                if (char.IsDigit(c))
                {
                    int s = i;
                    while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.')) i++;
                    list.Add(new Tok { Kind = TokKind.Number, Text = sql.Substring(s, i - s) });
                    continue;
                }

                if (char.IsLetter(c) || c == '_' || c == '@' || c == '$')
                {
                    int s = i;
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$' || sql[i] == '@')) i++;
                    list.Add(new Tok { Kind = TokKind.Word, Text = sql.Substring(s, i - s) });
                    continue;
                }

                list.Add(new Tok { Kind = TokKind.Symbol, Text = c.ToString() });
                i++;
            }
            return list;
        }

        private static int NextNonSpace(List<Tok> toks, int i)
        {
            while (i < toks.Count)
            {
                Tok t = toks[i];
                if (t.Kind == TokKind.Symbol && string.IsNullOrWhiteSpace(t.Text)) { i++; continue; }
                if (t.Kind == TokKind.Comment) { i++; continue; }
                return i;
            }
            return -1;
        }

        private static int MatchParen(List<Tok> toks, int openIdx)
        {
            int depth = 0;
            for (int i = openIdx; i < toks.Count; i++)
            {
                if (toks[i].Kind != TokKind.Symbol) continue;
                if (toks[i].Text == "(") depth++;
                else if (toks[i].Text == ")")
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static List<string> SplitArgs(List<Tok> toks, int openIdx, int closeIdx)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            int depth = 0;
            for (int i = openIdx + 1; i < closeIdx; i++)
            {
                Tok t = toks[i];
                if (t.Kind == TokKind.Symbol)
                {
                    if (t.Text == "(") depth++;
                    if (t.Text == ")") depth--;
                    if (t.Text == "," && depth == 0)
                    {
                        list.Add(sb.ToString());
                        sb.Length = 0;
                        continue;
                    }
                }
                sb.Append(t.Text);
            }
            if (sb.Length > 0 || list.Count > 0) list.Add(sb.ToString());
            return list;
        }
    }
}
