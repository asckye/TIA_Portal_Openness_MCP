# Table exchange fixtures

`tags-v14sp1.xml` and `tags-v21.xml` extend the import-tested layout in
`templates/mcp-full-e2e-verify/import/MCP_E2E_Tags.xml` with scalar access flags,
multilingual comments, user and system constants, and unrelated content to preserve.
They are synthetic SimaticML fixtures, not native exports.

`inline-text.xlsx` is emitted by `TableFile.WriteXlsx`. `shared-strings.xlsx` is a
hand-authored OOXML workbook with shared rich text, a numeric cell, a boolean and a
cached formula. Neither is claimed to have been saved by Excel. Add the lead's
Excel-saved real-export workbook when available and retain these regression fixtures.
