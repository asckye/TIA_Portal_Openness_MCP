# Table exchange fixtures

`tags-v14sp1.xml` and `tags-v21.xml` extend the import-tested layout in
`templates/mcp-full-e2e-verify/import/MCP_E2E_Tags.xml` with scalar access flags,
multilingual comments, user and system constants, and unrelated content to preserve.
They are synthetic SimaticML fixtures, not native exports.

`inline-text.xlsx` is emitted by `TableFile.WriteXlsx`. `shared-strings.xlsx` is a
hand-authored OOXML workbook with shared rich text, a numeric cell, a boolean and a
cached formula. Neither is claimed to have been saved by Excel. Add the lead's
Excel-saved real-export workbook when available and retain these regression fixtures.

`excel-escaped-strings.xlsx` reproduces the sharedStrings.xml and inline-string
escapes produced by an Excel save: `_x000D_` is CR, `_x005F_x0041_` is the
literal `_x0041_`, and `_x005F_x005F_` is the literal `_x005F_`. This is a
synthetic reproduction of the saved format, not a claim of a local Excel run.
