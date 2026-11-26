-- DBConvertDB 生成的目标库建表脚本
-- 目标方言：达梦 DM
-- 生成时间：2026-11-25 15:12:25

-- =============================================================
-- 源表：dbconvert_test.patient  →  目标表：PATIENT
--   id int  →  ID NUMBER(10)
--   name varchar(40)  →  NAME VARCHAR2(40)
--   gender varchar(1)  →  GENDER VARCHAR2(1)
--   birthday date  →  BIRTHDAY DATE
--   fee decimal(12,2)  →  FEE NUMBER(12,2)
--   ratio double  →  RATIO DOUBLE
--   memo longtext  →  MEMO CLOB
--   photo longblob  →  PHOTO BLOB
--   created datetime(6)  →  CREATED TIMESTAMP(6)
--   active tinyint(1)  →  ACTIVE NUMBER(1)
--   code char(36)  →  CODE CHAR(36)
-- =============================================================
CREATE TABLE "PATIENT" (
  "ID" NUMBER(10) IDENTITY(1,1) NOT NULL,
  "NAME" VARCHAR2(40) NOT NULL,
  "GENDER" VARCHAR2(1),
  "BIRTHDAY" DATE,
  "FEE" NUMBER(12,2),
  "RATIO" DOUBLE,
  "MEMO" CLOB,
  "PHOTO" BLOB,
  "CREATED" TIMESTAMP(6),
  "ACTIVE" NUMBER(1),
  "CODE" CHAR(36),
  CONSTRAINT "PATIENT_PK" PRIMARY KEY ("ID")
);

-- =============================================================
-- 源表：dbconvert_test.visit  →  目标表：VISIT
--   visit_no varchar(255)  →  VISIT_NO VARCHAR2(255)
--   patient_id int  →  PATIENT_ID NUMBER(10)
--   visit_time datetime(6)  →  VISIT_TIME TIMESTAMP(6)
--   amount decimal(10,2)  →  AMOUNT NUMBER(10,2)
--   remark varchar(200)  →  REMARK VARCHAR2(200)
-- =============================================================
CREATE TABLE "VISIT" (
  "VISIT_NO" VARCHAR2(255) NOT NULL,
  "PATIENT_ID" NUMBER(10) NOT NULL,
  "VISIT_TIME" TIMESTAMP(6),
  "AMOUNT" NUMBER(10,2),
  "REMARK" VARCHAR2(200),
  CONSTRAINT "VISIT_PK" PRIMARY KEY ("VISIT_NO")
);

