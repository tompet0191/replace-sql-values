# SQL Parameter Replacer

A native Windows desktop app that converts C# interpolated SQL strings into plain SQL ready to paste into any query window

## What it does

Paste a C# interpolated SQL string — the kind you'd find in a .NET repository using Dapper or ADO.NET — and the app will:

- Detect `{(int)Enum.Value}` **cast expressions** and let you fill in the integer value (or auto-fill by pasting the enum definition)
- Detect `{(condition ? "SQL fragment" : "")}` **ternary expressions**, group them by their boolean variable, and let you pick `true` / `false` from a dropdown
- Detect `@paramName` **SQL parameters** and let you type replacement values inline — list parameters (used after `IN`) auto-wrap `1,2,3` → `(1,2,3)`

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows (WPF app)

## Build & run

```bash
git clone https://github.com/tompet0191/replace-values-sql.git
cd replace-values-sql
dotnet run
```

Or build a self-contained executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The executable will be in `bin\Release\net8.0-windows\win-x64\publish\`.

## Usage

1. **Paste** your C# interpolated SQL into the top-left box (the `$@"..."` wrapper is stripped automatically)
2. Click **Parse Query** (`Ctrl+Enter`)
3. Optionally paste C# enum definitions into the bottom-left box and click **Apply Enums** to auto-fill cast expression values
4. Fill in the parameter values on the right — greyed-out rows won't appear in the output
5. Click **Generate SQL** (`Ctrl+Shift+Enter`)
6. Click **Copy to Clipboard** and paste into query window
