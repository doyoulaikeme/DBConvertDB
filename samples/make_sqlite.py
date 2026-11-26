import sqlite3, os, datetime
for f in ("samples/src.db", "samples/dst.db"):
    if os.path.exists(f): os.remove(f)

c = sqlite3.connect("samples/src.db")
c.executescript("""
CREATE TABLE patient (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name VARCHAR(40) NOT NULL,
  gender CHAR(1),
  birthday DATE,
  fee DECIMAL(12,2),
  ratio DOUBLE,
  memo TEXT,
  photo BLOB,
  created DATETIME,
  active BOOLEAN,
  code UUID
);
CREATE UNIQUE INDEX ux_patient_name ON patient(name);
CREATE INDEX ix_patient_birthday ON patient(birthday);
CREATE TABLE visit (
  visit_no TEXT PRIMARY KEY,
  patient_id INTEGER NOT NULL,
  visit_time DATETIME,
  amount NUMERIC(10,2),
  remark VARCHAR(200)
);
""")
rows = [
 (1,"张三","1","1980-02-03",1234.56,0.75,"慢性病复诊\n第二行", b"\x00\x01\xff", "2026-01-02 08:30:00", 1, "b1f0f1e2-1111-2222-3333-444455556666"),
 (2,"李四","2","1992-11-20",-8.90,1.5,"", b"abc", "2025-12-31 23:59:59", 0, None),
 (3,"王五","1",None,None,None,None,None,None,None,None),
 (4,"欧阳娜娜","2","2000-01-01",9999999999.99,3.14159,"x"*300,b"z"*1024,"2024-02-29 00:00:00.123",1,"00000000-0000-0000-0000-000000000000"),
]
c.executemany("INSERT INTO patient(id,name,gender,birthday,fee,ratio,memo,photo,created,active,code) VALUES (?,?,?,?,?,?,?,?,?,?,?)", rows)
c.executemany("INSERT INTO visit VALUES (?,?,?,?,?)", [
 ("V001",1,"2026-01-02 09:00:00.500",100.00,"首次"),
 ("V002",1,"2026-01-05 10:00:00",200.50,None),
 ("V003",2,"2026-02-01 11:11:11",0.01,""),
])
c.commit(); c.close()
print("source rows:", )

