# Elmah.ManagedErrorLog
Error Log for Elmah that uses Oracle.ManagedDataAcces client.
Based on original OracleErrorLog

# Usage 
Modify this string in web.config
```xml
<errorLog type="Elmah.ManagedOracleErrorLog, Elmah.ManagedOracleErrorLog" connectionStringName="elmah-oracle" />
```