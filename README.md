# Siphon

A simple command-line tool for extracting tables from Microsoft SQL Server and loading them into SQLite databases.

## The Problem

SQL Server is powerful, but sometimes you need your data somewhere more portable:

- **Development & Testing** - Spin up a local test environment without needing a full SQL Server instance
- **Data Analysis** - Share datasets with analysts who use SQLite tools like DB Browser or Datasette
- **Offline Access** - Take your data on the go without network dependencies
- **Cross-Platform Sharing** - SQLite runs everywhere - Windows, macOS, Linux, mobile, even in browsers
- **Archival** - Create portable snapshots of specific tables for long-term storage
- **Migration Prep** - Extract and inspect data before migrating to other systems

Siphon bridges this gap by making SQL Server-to-SQLite extraction simple and repeatable.

## Installation

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- Access to a SQL Server instance (local or remote)

### Build from Source

```bash
# Clone the repository
git clone https://github.com/bfowler/siphon.git
cd siphon

# Build the project
dotnet build

# Run the application
siphon --help
```

### Create a Release Build

```bash
# Build a self-contained executable for your platform
dotnet publish -c Release -r osx-arm64 --self-contained

# The executable will be in bin/Release/net10.0/osx-arm64/publish/
```

Available RIDs: `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64`

## Usage

### Command Line Options

| Option | Short | Description |
|--------|-------|-------------|
| `--server` | `-s` | SQL Server hostname or instance name |
| `--database` | `-d` | Source database name |
| `--table` | `-t` | Table name(s) to extract (supports wildcards: `*` and `?`) |
| `--output` | `-o` | Output SQLite file path |
| `--username` | `-u` | SQL authentication username (optional) |
| `--password` | `-p` | SQL authentication password (optional) |
| `--windows-auth` | `-w` | Use Windows integrated authentication |
| `--overwrite` | | Overwrite existing SQLite file |
| `--help` | `-h` | Show help information |

### Interactive Mode

If you omit required arguments, Siphon will prompt you for them:

```bash
siphon
# Prompts for: server, database, table(s), output path
# Prompts for credentials if not using Windows auth
```

### Examples

#### Extract a Single Table (Windows Authentication)

```bash
siphon -s localhost -d MyDatabase -t dbo.Customers -o data.db -w
```

#### Extract Multiple Tables

```bash
siphon -s localhost -d MyDatabase -t dbo.Customers -t dbo.Orders -t dbo.Products -o data.db -w
```

#### Use SQL Server Authentication

```bash
siphon -s myserver.example.com -d ProductionDB -t sales.Orders -o orders.db -u myuser -p mypassword
```

#### Specify Full Instance Name

```bash
siphon -s "MYSERVER\SQLEXPRESS" -d MyDatabase -t dbo.Users -o users.db -w
```

#### Overwrite an Existing File

```bash
siphon -s localhost -d MyDatabase -t dbo.Logs -o logs.db -w --overwrite
```

### Wildcard Table Selection

Use shell-style wildcards to match multiple tables at once:

| Wildcard | Meaning |
|----------|---------|
| `*` | Matches zero or more of any character |
| `?` | Matches exactly one of any character |

Patterns match against the full `schema.table` name.

Exact names without a schema use `dbo`. Repeated exact names and overlapping wildcard patterns export each source table once. If one pattern matches nothing but another selection finds tables, Siphon warns and exports the valid selection. If the entire selection is empty, or an exact table is missing, the command fails before opening the output file.

SQLite table names retain the source table name without its schema. Selecting `dbo.Customer` and `sales.Customer` together therefore fails with a collision error; export them to separate output files. Siphon also rejects table or column names that collide under SQLite's ASCII case-insensitive comparison and table names beginning with the reserved `sqlite_` prefix. These checks run before any output changes, including with `--overwrite`.

#### Extract All Tables in a Schema

```bash
siphon -s localhost -d MyDatabase -t sales.* -o sales_data.db -w
```

#### Extract Multiple Matching Tables

```bash
siphon -s localhost -d MyDatabase -t "*.Customer*" -o customers.db -w
```

#### Mix Exact Tables and Wildcards

```bash
siphon -s localhost -d MyDatabase -t dbo.Users -t sales.* -t reporting.Report* -o data.db -w
```

#### Single Character Wildcard

```bash
siphon -s localhost -d MyDatabase -t dbo.Table? -o tables.db -w
# Matches: dbo.Table1, dbo.Table2, dbo.TableA, etc.
```

## How It Works

1. **Connect** - Establishes a connection to your SQL Server instance using Windows or SQL authentication
2. **Validate Selection and Schema** - Resolves and deduplicates source tables, reads columns and primary keys, and checks destination names before opening the output
3. **Type Mapping** - Converts SQL Server data types to appropriate SQLite equivalents
4. **Create Destination** - Creates or updates the SQLite database file
5. **Transfer Data** - Streams explicitly selected columns, including hidden temporal period columns, in batches for efficient memory usage

### Exit Codes and Partial Output

Successful extraction returns `0`. Connection, selection-validation, and extraction failures return `1`. Invalid command-line arguments use the command-line parser's nonzero exit code. Summary table and row counts cover successfully completed tables; failed transfers are reported as errors.

Preflight failures leave an existing output file unchanged and do not create a missing output file or its parent directory. Once preflight succeeds, `--overwrite` deletes the previous database during initialization. A later transfer failure does not restore that file. Without `--overwrite`, selected destination tables are dropped and recreated while unrelated tables remain.

Transfers commit every 1,000 rows. A failure after transfer begins can leave completed tables and committed batches in a partial output database. Siphon returns a failure code and does not report successful extraction; it does not roll back the whole export or lock the source schema for the duration of the run.

### Running Tests

```powershell
dotnet restore .\siphon.slnx
dotnet build .\siphon.slnx --no-restore
dotnet test .\tests\Siphon.Tests\Siphon.Tests.csproj --no-build --filter 'Category!=SqlServerIntegration'
# Run separately on Windows with SqlLocalDB installed:
dotnet test .\tests\Siphon.Tests\Siphon.Tests.csproj --no-build --filter 'Category=SqlServerIntegration'
```

The SQLite/unit suite requires no SQL Server. The integration suite runs real CLI processes against synthetic data in a uniquely named LocalDB instance and removes its fixtures afterward. It does not use existing user databases or development secrets. Missing LocalDB is an explicit prerequisite failure. Run builds and test suites sequentially because Windows test hosts can lock their output assemblies.

### Type Mapping

| SQL Server Type | SQLite Type |
|-----------------|-------------|
| `bit`, `tinyint`, `smallint`, `int`, `bigint` | `INTEGER` |
| `decimal`, `numeric`, `money`, `float`, `real` | `REAL` |
| `char`, `varchar`, `nchar`, `nvarchar`, `text` | `TEXT` |
| `date`, `datetime`, `datetime2`, `smalldatetime` | `TEXT` (ISO8601) |
| `uniqueidentifier` | `TEXT` |
| `varbinary`, `binary` | `BLOB` |

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
