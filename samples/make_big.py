import random

path = r"samples/big.sql"
rows = 50000
with open(path, "w", encoding="utf-8", newline="\n") as f:
    f.write("DROP TABLE IF EXISTS big;\n")
    f.write("CREATE TABLE big (id INT PRIMARY KEY, name VARCHAR(60), amount DECIMAL(12,3), "
            "created DATETIME, memo TEXT, flag TINYINT(1)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;\n")
    batch = []
    for i in range(1, rows + 1):
        name = "患者%d号" % i
        if i % 97 == 0:
            name += "😀"
        memo = None if i % 13 == 0 else ("备注'%d\"测试" % i).replace("'", "''")
        batch.append("(%d,'%s',%0.3f,'2024-%02d-%02d %02d:%02d:%02d',%s,%d)" % (
            i, name, i * 1.25, (i % 12) + 1, (i % 28) + 1, i % 24, i % 60, i % 60,
            "NULL" if memo is None else "'" + memo.replace("\\", "\\\\") + "'",
            i % 2))
        if len(batch) >= 500:
            f.write("INSERT INTO big VALUES " + ",".join(batch) + ";\n")
            batch = []
    if batch:
        f.write("INSERT INTO big VALUES " + ",".join(batch) + ";\n")
print("written", path)
