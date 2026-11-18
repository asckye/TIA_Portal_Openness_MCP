# Table exchange fixtures

`tags-v14sp1.xml` and `tags-v21.xml` extend the import-tested layout in
`templates/mcp-full-e2e-verify/import/MCP_E2E_Tags.xml` with scalar access flags,
multilingual comments, user and system constants, and unrelated content to preserve.
They are synthetic SimaticML fixtures, not native exports.

`db-v15.1.xml` and `db-v21.xml` extend
[`DB_HMI_Interface.xml`](../../../../../templates/plc/block-xml/DB_HMI_Interface.xml)
with nested structs, an array subelement, default and explicit start values,
Chinese and escaped names, informative members, and structured data types.
They use the official interface v3 (V15.1) and v5 (V21) layouts. These are
synthetic fixtures; add the lead's real DB exports when available and keep both.

`db-review-v15.1.xml` and `db-review-v21.xml` extend those DB fixtures with member
attributes, multilingual comments, retain flags, block title/comment objects,
constant-reference and bulk StartValue attributes, a nested UDT, and existing
HW_IO/DB_ANY and String[constant] scalar literals. The table route refuses
ConstantName/IsBulkValue and explains every excluded member in notEditableReasons.
The additional existing-value allow-list is HW_IO, HW_ANY, HW_DEVICE, HW_MODULE,
HW_SUBMODULE, DB_ANY and String/WString[constant]; missing values on these types
stay excluded. These remain synthetic fixtures, not real export evidence.

`inline-text.xlsx` is emitted by `TableFile.WriteXlsx`. `shared-strings.xlsx` is a
hand-authored OOXML workbook with shared rich text, a numeric cell, a boolean and a
cached formula. Neither is claimed to have been saved by Excel. Add the lead's
Excel-saved real-export workbook when available and retain these regression fixtures.

`excel-escaped-strings.xlsx` reproduces the sharedStrings.xml and inline-string
escapes produced by an Excel save: `_x000D_` is CR, `_x005F_x0041_` is the
literal `_x0041_`, and `_x005F_x005F_` is the literal `_x005F_`. This is a
synthetic reproduction of the saved format, not a claim of a local Excel run.

`shared-logical-limit.xlsx` is a synthetic workbook with 2,050 cells referencing
one 32,767-character shared string. It verifies reuse of decoded shared strings
and rejection at the 64 Mi logical character limit despite small ZIP/XML parts.
