using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DbConvert
{
    public enum IssueLevel
    {
        /// <summary>对象未能生成/执行，属于阻断项。</summary>
        Blocking,
        /// <summary>已生成但需要人工核对（改写、语法骨架）。</summary>
        Manual,
        /// <summary>自动改写，语义基本等价，建议抽查。</summary>
        Rewritten,
        /// <summary>明确不支持，已跳过并给出替代建议。</summary>
        Skipped
    }

    public class ObjectIssue
    {
        public string ObjectType = "";
        public string ObjectName = "";
        public IssueLevel Level = IssueLevel.Manual;
        public string Message = "";
        public string Source = "";
        public string Result = "";
        public string Suggestion = "";

        public string LevelText
        {
            get
            {
                switch (Level)
                {
                    case IssueLevel.Blocking: return "阻断";
                    case IssueLevel.Manual: return "需人工";
                    case IssueLevel.Rewritten: return "已改写";
                    default: return "已跳过";
                }
            }
        }
    }

    /// <summary>全对象迁移的问题清单：所有未复现/被改写/需人工的点都必须落在这里。</summary>
    public class IssueLog
    {
        private readonly List<ObjectIssue> _items = new List<ObjectIssue>();
        private readonly HashSet<string> _dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ObjectIssue> Items => _items;

        public void Add(string objectType, string objectName, IssueLevel level, string message,
                        string source = "", string result = "", string suggestion = "")
        {
            string key = objectType + "|" + objectName + "|" + level + "|" + message;
            if (!_dedup.Add(key)) return;
            _items.Add(new ObjectIssue
            {
                ObjectType = objectType,
                ObjectName = objectName,
                Level = level,
                Message = message,
                Source = source ?? "",
                Result = result ?? "",
                Suggestion = suggestion ?? ""
            });
        }

        public int Count(IssueLevel level)
        {
            int n = 0;
            foreach (ObjectIssue i in _items) if (i.Level == level) n++;
            return n;
        }

        public int CountOf(string objectType)
        {
            int n = 0;
            foreach (ObjectIssue i in _items) if (string.Equals(i.ObjectType, objectType, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        public void WriteCsv(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("对象类型,对象名,级别,说明,原文,改写后,建议");
            foreach (ObjectIssue i in _items)
            {
                sb.Append(Csv(i.ObjectType)).Append(',')
                  .Append(Csv(i.ObjectName)).Append(',')
                  .Append(Csv(i.LevelText)).Append(',')
                  .Append(Csv(i.Message)).Append(',')
                  .Append(Csv(i.Source)).Append(',')
                  .Append(Csv(i.Result)).Append(',')
                  .Append(Csv(i.Suggestion)).AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public void AppendMarkdown(StringBuilder sb)
        {
            if (_items.Count == 0) return;
            sb.AppendLine("## 对象迁移问题清单");
            sb.AppendLine();
            sb.AppendLine("- 阻断：" + Count(IssueLevel.Blocking) + " 项");
            sb.AppendLine("- 需人工：" + Count(IssueLevel.Manual) + " 项");
            sb.AppendLine("- 已自动改写：" + Count(IssueLevel.Rewritten) + " 项");
            sb.AppendLine("- 已跳过：" + Count(IssueLevel.Skipped) + " 项");
            sb.AppendLine();
            sb.AppendLine("| 对象 | 类型 | 级别 | 说明 | 建议 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (ObjectIssue i in _items)
            {
                sb.AppendLine("| " + MdCell(i.ObjectName) + " | " + MdCell(i.ObjectType) + " | " +
                              MdCell(i.LevelText) + " | " + MdCell(i.Message) + " | " +
                              MdCell(i.Suggestion) + " |");
            }
            sb.AppendLine();
        }

        private static string MdCell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
