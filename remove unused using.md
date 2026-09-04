Yes. In a .NET project, you can automatically detect and remove unused `using` directives, and you can enforce it across the whole repository.

The cleanest approach is to combine **`.editorconfig` + `dotnet format`**.

### 1. Add this to `.editorconfig`

```ini
[*.cs]
# Remove unnecessary using directives
dotnet_diagnostic.IDE0005.severity = warning
```

`IDE0005` is the analyzer rule for unnecessary `using` directives.

For example:

```csharp
using System;
using System.Collections.Generic;
using System.Linq; // unused

public class UserService
{
    public List<string> GetUsers()
    {
        return new List<string>();
    }
}
```

The `System.Linq` import will be detected as unnecessary.

### 2. Remove them automatically

From your solution/project directory:

```bash
dotnet format
```

This applies supported formatting/analyzer fixes, including unnecessary imports.

You can also target the analyzer specifically:

```bash
dotnet format analyzers --diagnostics IDE0005
```

So you could have a command such as:

```bash
dotnet format analyzers --diagnostics IDE0005
```

to clean unused `using`s throughout the solution.

### 3. Make CI fail when unused usings exist

This is useful if you want the project to stay clean:

```bash
dotnet format --verify-no-changes
```

Your CI flow could conceptually be:

```text
Developer code
      │
      ▼
 dotnet format
      │
      ▼
 dotnet build
      │
      ▼
 dotnet test
```

And CI:

```bash
dotnet format --verify-no-changes
dotnet build --no-restore
dotnet test --no-build
```

If formatting is incorrect, CI fails instead of silently accepting it.

### 4. Visual Studio can also do it

Visual Studio has:

**Edit → IntelliSense → Remove and Sort Usings**

You can also configure **Code Cleanup** so that saving/cleaning a file performs:

```text
Remove unnecessary usings
Sort usings
Apply code style fixes
```

For VS Code with C# tooling, you can similarly use organize imports / code actions.

### What I'd put in a real project

At the root:

```text
MySolution/
├── .editorconfig
├── MySolution.sln
├── src/
└── tests/
```

`.editorconfig`:

```ini
root = true

[*.cs]

# Unused imports
dotnet_diagnostic.IDE0005.severity = warning

# Put System.* usings first
dotnet_sort_system_directives_first = true

# Separate using groups
dotnet_separate_import_directive_groups = false
```

Then developers can simply run:

```bash
dotnet format
```

before committing.

If you want unused `using`s to be treated more strictly, change:

```ini
dotnet_diagnostic.IDE0005.severity = warning
```

to:

```ini
dotnet_diagnostic.IDE0005.severity = error
```

Then your build/CI policy can reject code containing unnecessary imports.

For most projects, I'd use **warning + `dotnet format --verify-no-changes` in CI** rather than turning IDE0005 into a compiler-style error.
