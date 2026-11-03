@echo off
rem Sample console app: writes the orders CSV report next to this script (..\Output).
if not exist "%~dp0..\Output" mkdir "%~dp0..\Output"
(
echo Cust Id,FullName,Email,OrderDate,Amount,Currency
echo 1001,Alice Tan,alice@example.com,2026-10-01,120.50,USD
echo 1002,Bob Lee,bob@example.com,2026-10-02,75.00,USD
echo 1003,Carol Wong,carol@example.com,2026-10-03,99.99,USD
) > "%~dp0..\Output\orders_report.csv"
echo Report created
exit /b 0
