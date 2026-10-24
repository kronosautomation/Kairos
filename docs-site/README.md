# Documentation site sources

Built with [DocFX](https://dotnet.github.io/docfx/). Conceptual pages are Markdown in this folder; the API reference is generated
from the XML comments of the public packages (see `docfx.json`).

```
dotnet tool restore
dotnet docfx docs-site/docfx.json --warningsAsErrors
```

Rules for every page here:

- Describe behaviour and guarantees, including their limits. Do not describe plans, and do not link to anything outside this folder
  and `samples/`.
- Code comes from the compiling projects under `samples/`, quoted by region: `[!code-csharp[](../../samples/Project/File.cs#region)]`.
  Never type code into a page.
- The product name is written "Kairos" throughout; if it changes, search for it here and in `docfx.json` (`_appName`).
- `build/check-disclosure.ps1` runs before the build and fails on forbidden references and terms. Fix the text, do not work around the check.
