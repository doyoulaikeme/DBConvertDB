DROP VIEW IF EXISTS v_demo_emp;
DROP TABLE IF EXISTS demo_emp;
DROP TABLE IF EXISTS demo_dept;
DROP FUNCTION IF EXISTS fn_tax;
DROP PROCEDURE IF EXISTS sp_demo;
DROP TRIGGER IF EXISTS trg_demo_bi;

CREATE TABLE demo_dept (
  id INT NOT NULL AUTO_INCREMENT,
  name VARCHAR(50) NOT NULL,
  budget DECIMAL(10,2) DEFAULT 0.00,
  PRIMARY KEY (id)
) COMMENT='部门';

CREATE TABLE demo_emp (
  id INT NOT NULL AUTO_INCREMENT,
  dept_id INT NOT NULL,
  name VARCHAR(30) NOT NULL DEFAULT '无',
  salary DECIMAL(10,2) NOT NULL,
  hired DATETIME DEFAULT CURRENT_TIMESTAMP,
  memo VARCHAR(100) DEFAULT NULL,
  total DECIMAL(12,2) GENERATED ALWAYS AS (salary * 1.2) STORED,
  PRIMARY KEY (id),
  KEY ix_emp_dept (dept_id),
  CONSTRAINT ck_salary CHECK (salary >= 0),
  CONSTRAINT fk_emp_dept FOREIGN KEY (dept_id) REFERENCES demo_dept (id) ON DELETE CASCADE
) COMMENT='员工';

INSERT INTO demo_dept(name, budget) VALUES ('药库', 1000), ('门诊', 500);
INSERT INTO demo_emp(dept_id, name, salary, memo) VALUES (1,'张三',5000,NULL),(2,'李四',3000,'备注');

CREATE VIEW v_demo_emp AS
SELECT e.id, e.name, IFNULL(e.memo,'-') AS memo_text,
       DATE_FORMAT(e.hired,'%Y-%m-%d') AS hired_day,
       CONCAT(e.name,'/',e.dept_id) AS tag,
       e.salary
FROM demo_emp e
WHERE e.salary > 0;

DELIMITER $$
CREATE FUNCTION fn_tax(p DECIMAL(10,2)) RETURNS DECIMAL(10,2) DETERMINISTIC
BEGIN
  RETURN p * 0.06;
END$$

CREATE PROCEDURE sp_demo(IN x INT)
BEGIN
  SELECT x + 1 AS next_val;
END$$

CREATE TRIGGER trg_demo_bi BEFORE INSERT ON demo_emp FOR EACH ROW
BEGIN
  SET NEW.memo = IFNULL(NEW.memo, 'auto');
END$$
DELIMITER ;

SELECT 'ok' AS result;