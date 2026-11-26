-- DBConvertDB 生成的目标库建表脚本
-- 目标方言：SQL Server
-- 生成时间：2026-11-25 15:12:34

-- =============================================================
-- 源表：dbconvert_test.patient  →  目标表：dbo.patient
--   id int  →  id INT
--   name varchar(40)  →  name NVARCHAR(40)
--   gender varchar(1)  →  gender NVARCHAR(1)
--   birthday date  →  birthday DATE
--   fee decimal(12,2)  →  fee DECIMAL(12,2)
--   ratio double  →  ratio FLOAT(53)
--   memo longtext  →  memo NVARCHAR(MAX)
--   photo longblob  →  photo VARBINARY(MAX)
--   created datetime(6)  →  created DATETIME2(6)
--   active tinyint(1)  →  active BIT
--   code char(36)  →  code NCHAR(36)
-- =============================================================
CREATE TABLE [dbo].[patient] (
  [id] INT IDENTITY(1,1) NOT NULL,
  [name] NVARCHAR(40) NOT NULL,
  [gender] NVARCHAR(1),
  [birthday] DATE,
  [fee] DECIMAL(12,2),
  [ratio] FLOAT(53),
  [memo] NVARCHAR(MAX),
  [photo] VARBINARY(MAX),
  [created] DATETIME2(6),
  [active] BIT,
  [code] NCHAR(36),
  CONSTRAINT [patient_PK] PRIMARY KEY ([id])
);

-- =============================================================
-- 源表：dbconvert_test.visit  →  目标表：dbo.visit
--   visit_no varchar(255)  →  visit_no NVARCHAR(255)
--   patient_id int  →  patient_id INT
--   visit_time datetime(6)  →  visit_time DATETIME2(6)
--   amount decimal(10,2)  →  amount DECIMAL(10,2)
--   remark varchar(200)  →  remark NVARCHAR(200)
-- =============================================================
CREATE TABLE [dbo].[visit] (
  [visit_no] NVARCHAR(255) NOT NULL,
  [patient_id] INT NOT NULL,
  [visit_time] DATETIME2(6),
  [amount] DECIMAL(10,2),
  [remark] NVARCHAR(200),
  CONSTRAINT [visit_PK] PRIMARY KEY ([visit_no])
);

