import sqlite3, sys
db = sys.argv[1]
c = sqlite3.connect(db)
print("== sqlite_master ==")
for name, sql in c.execute("select name, sql from sqlite_master where type in ('table','view','index')"):
    print(name, "|", sql)
print("== demo_emp rows ==")
for r in c.execute("select * from demo_emp"):
    print(r)
print("== view ==")
for r in c.execute("select * from v_demo_emp"):
    print(r)
