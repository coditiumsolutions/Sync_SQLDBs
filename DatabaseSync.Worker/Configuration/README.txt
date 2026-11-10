Database connection credentials should not be stored in source control for production.

Preferred approaches:
1. User Secrets (development)
   dotnet user-secrets set "DatabaseConnections:MainDatabase:Password" "YOUR_PASSWORD" --project DatabaseSync.Worker

2. Environment variables (production / Windows Service)
   DatabaseConnections__MainDatabase__Password
   DatabaseConnections__CloudDatabase__Password
   DatabaseConnections__LocalDatabase__Password

   Or full connection property overrides:
   DatabaseConnections__MainDatabase__Server
   DatabaseConnections__MainDatabase__Database
   DatabaseConnections__MainDatabase__UserId
   DatabaseConnections__MainDatabase__IntegratedSecurity
