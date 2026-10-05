# SimaticML Reading Guide

> **Technical format reference.** This guide records schema and sample observations; it is not a
> support matrix. See [CAPABILITIES.md](CAPABILITIES.md) for current implementation and
> qualification status.

This guide describes how to read SimaticML block exports from TIA Portal.
The findings are based on analysis of four sample blocks and all official
Siemens XSD schemas for PLC block XML.

| File | Block name | Block number | Block kind | Language | Networks | Key features |
| --- | --- | ---: | --- | --- | ---: | --- |
| `deviceState.xml` | `deviceState` | 904 | FC | LAD | 1 | Move, Add, contacts, DisabledENO |
| `SimpleDevice.xml` | `SimpleDevice` | 580 | FC | LAD | 2 | Coil, OR, Negated contact, user FC call |
| `SingleAlarm_FB.xml` | `SingleAlarm_FB` | 905 | FB | LAD | 3 | RS, TON, PBox, Inc, comparisons, Static section |
| `FB_SYSTEM.xml` | `FB_SYSTEM` | 800 | FB | LAD | 8 | SCL network, UDT, bit slice, PContact, RCoil, system FC |

Schema files consulted:

| Schema file | Covers |
| --- | --- |
| `SW.PlcBlocks.Access_v5` | Access nodes, symbols, constants, addresses, scopes, types |
| `SW.PlcBlocks.CompileUnitCommon_v5` | Label declarations, shared compile-unit elements |
| `SW.PlcBlocks.LADFBD_v5` | FlgNet, Parts, Wires, Calls for LAD/FBD |
| `SW.PlcBlocks.SCL_v4` | StructuredText tokenized representation |
| `SW.PlcBlocks.STL_v5` | StatementList, STL instructions |
| `SW.PlcBlocks.Graph_v6` | GRAPH/SFC steps, transitions, branches, actions |
| `SW.PlcBlocks.TypeSupervisions_v4` | Block-level supervision definitions |
| `SW.PlcBlocks.PLCDataTypeSupervisions_v2` | UDT-level supervision definitions |
| `SW.PlcBlocks.InstSupervisions` | Instance-level supervision linking |

## Read Order

Read a SimaticML block export in this order:

1. `Document/Engineering`
2. `Document/SW.Blocks.*`
3. `SW.Blocks.*/AttributeList`
4. `AttributeList/Interface`
5. `ObjectList/SW.Blocks.CompileUnit`
6. Compile unit `NetworkSource`
7. Compile unit multilingual title/comment metadata

This order separates project/export metadata, block metadata, declaration data,
and executable network data.

## TIA Version

The TIA Portal export version is stored near the top of the file:

```xml
<Engineering version="V21" />
```

Treat this as the schema/export version signal. It is not the block language and
not the PLC firmware version.

## Block Object

The block is represented by an `SW.Blocks.*` element:

```xml
<SW.Blocks.FC ID="0">
```

The suffix identifies the block kind:

| Element | Meaning |
| --- | --- |
| `SW.Blocks.FC` | Function |
| `SW.Blocks.FB` | Function block |
| `SW.Blocks.OB` | Organization block, expected in other exports |
| `SW.Blocks.DB` | Data block, expected in other exports |

The `ID` is an internal XML reference id. Use it for resolving references inside
the same export, not as a stable user-facing block identity. IDs are
hex-sequential (after `"9"` comes `"A"`, `"B"`, etc.), scoped per document.

### Block Types in CallInfo

When a block is called from within a network, the `CallInfo` element uses a
`BlockType` attribute. The schema defines these values:

| BlockType | Meaning |
| --- | --- |
| `FC` | Function |
| `FB` | Function block |
| `OB` | Organization block |
| `DB` | Data block |
| `UDT` | User-defined type |
| `FBT` | Function block template (technology) |
| `FCT` | Function template (technology) |

`FBT` and `FCT` are technology-related template block types. A parser should
accept them even if they are not common in typical project exports.

### FC vs FB Differences

| Property | FC | FB |
| --- | --- | --- |
| Block element | `SW.Blocks.FC` | `SW.Blocks.FB` |
| Static section | Always empty | May contain members |
| Return section | Present, `Ret_Val` of `Void` | Absent |
| `MemoryReserve` | Absent | Present (e.g. `100`) |
| Instance data | None | Backed by instance DB |

## Block Attributes

Block properties live under the block's `AttributeList`.

Observed properties:

| Property | Example | Meaning |
| --- | --- | --- |
| `Name` | `deviceState` | Block name shown in TIA Portal |
| `Number` | `904` | Block number |
| `ProgrammingLanguage` | `LAD` | Main implementation language |
| `MemoryLayout` | `Optimized` | Optimized/unoptimized access style |
| `MemoryReserve` | `100` | FB memory reserve percentage (FB only) |
| `SetENOAutomatically` | `false` | ENO handling option |
| `AutoNumber` | `false` | Whether TIA auto-assigned the number |
| `Namespace` | empty | Optional namespace/grouping metadata |
| `Interface` | nested XML | Block declaration section, not plain text |

Do not read `Interface` as a scalar property. It contains the declaration tree.

## Interface

`Interface` describes the block signature and local declarations.

The declaration sections are stored as:

```xml
<Interface>
  <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
    <Section Name="Input">
      <Member Name="Alarm" Datatype="Bool" />
    </Section>
  </Sections>
</Interface>
```

Important detail: `Sections` uses the Siemens interface namespace. XML parsers
must be namespace-aware or query by local name.

### Interface Sections

| Section | Meaning | FC | FB |
| --- | --- | --- | --- |
| `Input` | Input parameters | Yes | Yes |
| `Output` | Output parameters | Yes | Yes |
| `InOut` | In/out parameters passed by reference | Yes | Yes |
| `Static` | Instance data persisting across calls | Empty | Yes |
| `Temp` | Temporary local variables | Yes | Yes |
| `Constant` | Local constants | Yes | Yes |
| `Return` | Function return value (`Ret_Val`) | Yes (Void) | Absent |

### Member Attributes

Each `Member` has at least `Name` and `Datatype`. Additional attributes and
child elements appear depending on context:

| Attribute/Element | Example | Meaning |
| --- | --- | --- |
| `Name` | `Alarm` | Variable/parameter name |
| `Datatype` | `Bool`, `USInt`, `Time` | TIA datatype |
| `Accessibility` | — | Optional visibility/access modifier |
| `Remanence` | `Retain` | Retentive flag, survives power cycle |
| `Version` | `1.0` | Type definition version (system/complex types) |
| `StartValue` (child) | `T#3s`, `16#0` | Default/initial value in TIA literal format |
| `AttributeList` (child) | see below | System metadata (SetPoint, etc.) |

#### StartValue

Members can have explicit default values:

```xml
<Member Name="Reset_A_time" Datatype="Time">
  <StartValue>T#3s</StartValue>
</Member>
```

Members without `StartValue` have no explicit default (TIA uses type defaults).
Values use TIA Portal literal format: `T#3s` for time, `16#0` for hex, `false`
for Bool, etc.

#### BooleanAttribute on Members

Static members can carry system metadata:

```xml
<Member Name="TON_Reset_A" Datatype="TON_TIME" Version="1.0">
  <AttributeList>
    <BooleanAttribute Name="SetPoint" SystemDefined="true">true</BooleanAttribute>
  </AttributeList>
</Member>
```

`SystemDefined="true"` indicates system-managed metadata. This corresponds to
checkbox columns in TIA Portal (SetPoint, Accessible, Visible, Writable, etc.).

### UDT Type References

User-defined types use quoted datatype names:

```xml
<Member Name="System" Datatype="&quot;PLC_System&quot;" />
```

The `&quot;` is XML-escaped double quotes. The actual datatype is
`"PLC_System"`. Quotes distinguish UDT references from built-in types. A parser
must unescape and recognize quoted type names as UDT references.

### System Type References

System types like timers carry a version:

```xml
<Member Name="Timer" Datatype="IEC_TIMER" Version="1.0" />
<Member Name="TON_Reset_A" Datatype="TON_TIME" Version="1.0" />
```

Both `IEC_TIMER` (generic) and `TON_TIME` (specific) are valid backing types
for TON timer instances.

### Array Types

Array types are specified inline in the `Datatype` attribute:

```xml
<Member Name="System_CLK_temp" Datatype="Array[0..7] of Bool" />
```

## Compile Units And Networks

Executable logic is stored below `ObjectList` as one or more
`SW.Blocks.CompileUnit` objects.

Each compile unit has its own `AttributeList`, including:

| Property | Meaning |
| --- | --- |
| `ProgrammingLanguage` | Network language (see table below) |
| `NetworkSource` | The executable network content |

### Programming Languages

The schema defines the complete set of programming language values:

| Value | Meaning | NetworkSource container |
| --- | --- | --- |
| `LAD` | Ladder Diagram | `FlgNet` |
| `FBD` | Function Block Diagram | `FlgNet` |
| `LAD_IEC` | LAD (IEC variant) | `FlgNet` |
| `FBD_IEC` | FBD (IEC variant) | `FlgNet` |
| `SCL` | Structured Control Language | `StructuredText` |
| `STL` | Statement List | `StatementList` |
| `GRAPH` | Sequential Function Chart | `Graph` |
| `DB` | Data block (no executable logic) | — |
| `SDB` | System data block | — |
| `DB_CPU` | CPU data block | — |
| `FB_IDB` | FB instance data block | — |
| `SFB_IDB` | System FB instance data block | — |
| `DT_DB` | Data type data block | — |

A single block can mix languages: the block-level `ProgrammingLanguage` is
`LAD`, but individual compile units can set `SCL` or other languages. Always
check the per-compile-unit language.

Treat each compile unit as one network when building a reader or UI.

### Empty Networks

A compile unit may have an empty `NetworkSource`:

```xml
<NetworkSource />
```

This represents a blank network with no logic (just a power rail line in TIA
Portal). A parser must handle the case where `NetworkSource` has no child
elements.

### UId Scope

UIds are only unique within a single compile unit, not across the entire block.
Multiple compile units commonly reuse the same UId values (e.g. both starting at
21). A parser must scope its UId lookup table per compile unit.

## NetworkSource — LAD/FBD Networks

LAD/FBD networks use the `FlgNet` container:

```xml
<NetworkSource>
  <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">
    <Labels> ... </Labels>
    <Parts> ... </Parts>
    <Wires> ... </Wires>
  </FlgNet>
</NetworkSource>
```

The network is graph-shaped, not line-shaped. `FlgNet` contains three sections:
`Labels` (optional jump target declarations), `Parts` (data, instructions, and
calls), and `Wires` (connections between them).

`Parts` holds three categories of elements: Access nodes (data), Part nodes
(instructions), and Call nodes (user block calls).

### Labels in FlgNet

`FlgNet` can optionally contain a `Labels` section with `LabelDeclaration`
elements for jump targets within the network:

```xml
<Labels>
  <LabelDeclaration UId="10">
    <Label Name="Loop_Start" />
  </LabelDeclaration>
</Labels>
```

Labels may have an `IntegerAttribute` for `NumBLs` (informative) and optional
comments. The colon separator between label name and code is stored as a `Token`
child when present.

### Access Nodes

Access nodes represent data references — variables, constants, addresses, and
more. Each access node has a `Scope` attribute that determines its child
element and meaning.

```xml
<Access Scope="LocalVariable" UId="22">
  <Symbol>
    <Component Name="deviceIcon" />
  </Symbol>
</Access>

<Access Scope="LiteralConstant" UId="21">
  <Constant>
    <ConstantType>Int</ConstantType>
    <ConstantValue>0</ConstantValue>
  </Constant>
</Access>
```

#### Access Scope

The `Scope` attribute determines what kind of data the access node references.
The schema defines 20 scope values:

| Scope | Meaning | Typical child | LAD/FBD |
| --- | --- | --- | --- |
| `LocalVariable` | Block interface or local variable | `Symbol` | Yes |
| `GlobalVariable` | Cross-block variable (e.g. DB field, PLC tag) | `Symbol` | Yes |
| `LiteralConstant` | Inline constant value | `Constant` | Yes |
| `LocalConstant` | Block-local constant | `Constant` or `Symbol` | Yes |
| `GlobalConstant` | Project-wide constant | `Constant` or `Symbol` | Yes |
| `TypedConstant` | Typed constant with explicit type | `Constant` | Yes |
| `AddressConstant` | Pointer/address constant | `Constant` | Rare |
| `AlarmConstant` | Alarm-related constant | `Constant` | Rare |
| `NamedValueConstant` | Named enum-style constant | `Constant` | Rare |
| `Address` | Absolute memory address | `Address` | Yes |
| `Instruction` | System instruction reference | `Instruction` | SCL/STL |
| `Label` | Jump target label | `Label` | SCL/STL |
| `Call` | User block call | `CallInfo` | SCL/STL |
| `CallWithType` | Typed block call | `CallInfo` | SCL/STL |
| `UserType` | User-defined type reference | `DataType` | Rare |
| `SystemType` | System type reference | `DataType` | Rare |
| `Expression` | Composed SCL expression | `Expression` | SCL only |
| `Statusword` | CPU status word access | `Statusword` | S7-300/400 |
| `PredefinedVariable` | Built-in variable (currently only `ENO`) | `PredefinedVariable` | SCL only |
| `Reference` | Reference-type access | `Reference` | Rare |
| `Undef` | Unknown/unresolved symbol | — | Rare |
| `Unnamed` | Unnamed/anonymous access | — | Rare |

For a LAD/FBD-focused parser, `LocalVariable`, `GlobalVariable`,
`LiteralConstant`, and `Address` are the most common. The SCL/STL-specific
scopes (`Instruction`, `Label`, `Call`, `Expression`, `PredefinedVariable`)
appear only in those language containers.

#### Access Child Elements

Each scope maps to a specific child element. The full set of possible children:

| Child element | Used by scopes | Meaning |
| --- | --- | --- |
| `Symbol` | `LocalVariable`, `GlobalVariable` | Variable reference via component chain |
| `Constant` | `LiteralConstant`, `LocalConstant`, `GlobalConstant`, `TypedConstant`, `AddressConstant`, `AlarmConstant`, `NamedValueConstant` | Constant value with type and format |
| `Address` | `Address` | Absolute memory address with area and offset |
| `CallInfo` | `Call`, `CallWithType` | User block call (SCL/STL only, not LAD/FBD) |
| `Instruction` | `Instruction` | System instruction call (SCL/STL only) |
| `Label` | `Label` | Jump target label (SCL/STL) |
| `Indirect` | (STL) | Indirect addressing via address register (STL only) |
| `Statusword` | `Statusword` | CPU status word bits (S7-300/400/WinAC only) |
| `PredefinedVariable` | `PredefinedVariable` | Built-in variable, currently only `ENO` (SCL only) |
| `Expression` | `Expression` | Composed expression of Access + Token sequences (SCL only) |
| `DataType` | `UserType`, `SystemType` | Type reference |
| `Reference` | `Reference` | Reference-type access |

An Access node always has exactly one child from this list (chosen by scope),
optionally followed by a comment.

#### The `Informative` Attribute

Several elements (`Address`, `ConstantType`, `ConstantValue`, `DataType`,
`Instruction`, `Parameter`) can carry an `Informative` attribute:

```xml
<ConstantType Informative="true">Int</ConstantType>
```

When `Informative="true"`, the value is display metadata only — the import
process ignores it. The authoritative value is computed from context. A parser
should still read informative values (they provide useful debug information)
but must not rely on them for correctness.

#### Duplicate Access Nodes

Each use of a variable in a network gets its own Access node with a unique UId.
For example, `deviceIcon` can appear as nine separate Access nodes in one
network. Variables are not shared references in the graph. A parser must not
deduplicate them or assume one Access node per variable.

#### Multi-Component Symbols — Struct/UDT Field Access

Symbols with multiple `Component` elements represent hierarchical access:

```xml
<Access Scope="LocalVariable" UId="22">
  <Symbol>
    <Component Name="System" />
    <Component Name="CLK100ms" />
  </Symbol>
</Access>
```

This represents `#System.CLK100ms` — access into a UDT field. The component
chain can go deeper for nested structs. Each `Component` is one level in the
dot-separated path.

#### Component Attributes

Each `Component` element carries several attributes beyond `Name`:

| Attribute | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Name` | string | required | Field/variable name |
| `SliceAccessModifier` | pattern | `undef` | Bit/byte/word/dword slice |
| `AccessModifier` | enum | `None` | Array or reference access mode |
| `SimpleAccessModifier` | pattern | `None` | Periphery or quality information access |

**`SliceAccessModifier`** — the schema confirms the regex pattern
`([xbwdXBWD]\d+)|undef`:

| Prefix | Slice size | Example | TIA display |
| --- | --- | --- | --- |
| `x` or `X` | Bit | `x0`, `x15` | `.%X0`, `.%X15` |
| `b` or `B` | Byte | `b0`, `b1` | `.%B0`, `.%B1` |
| `w` or `W` | Word | `w0`, `w1` | `.%W0`, `.%W1` |
| `d` or `D` | DWord | `d0` | `.%D0` |

The prefix letter is case-insensitive. The number after the prefix is the
slice index. The `%X`/`%B`/`%W`/`%D` display prefix is a TIA Portal convention
not stored in the XML.

```xml
<Component Name="Clock_Byte" SliceAccessModifier="x0" />
```

This represents `#Clock_Byte.%X0` in TIA Portal.

**`AccessModifier`** — controls how the component is accessed:

| Value | Meaning |
| --- | --- |
| `None` | Direct access (default) |
| `Array` | Array indexing — component has child `Access` elements for indices |
| `Reference` | Dereferenced access through a reference |
| `ReferenceToArray` | Dereferenced access to an array element |

When `AccessModifier="Array"`, the component contains child `Access` elements
representing the array subscripts:

```xml
<Component Name="MyArray" AccessModifier="Array">
  <Access Scope="LiteralConstant" UId="30">
    <Constant>
      <ConstantType>DInt</ConstantType>
      <ConstantValue>5</ConstantValue>
    </Constant>
  </Access>
</Component>
```

This represents `#MyArray[5]`.

**`SimpleAccessModifier`** — a comma-separated combination of access qualifiers:

| Value | Meaning |
| --- | --- |
| `None` | Standard access (default) |
| `Periphery` | Direct I/O periphery access (bypasses process image) |
| `QualityInformation` | Quality information access (diagnostics) |

Values can be combined: `Periphery, QualityInformation`.

#### Absolute Addressing — The `Address` Element

When `Scope="Address"`, the child is an `Address` element representing an
absolute memory reference (e.g. `%MW100`, `%I0.0`, `DB10.DBW0`):

```xml
<Access Scope="Address" UId="25">
  <Address Area="Memory" Type="Word" BitOffset="800" />
</Access>
```

The `BitOffset` is calculated as `Byte * 8 + Bit`. In this example,
`BitOffset="800"` means byte 100, bit 0 → `%MW100`.

| Attribute | Type | Meaning |
| --- | --- | --- |
| `Area` | enum | Memory area (see table below) |
| `Type` | string | Data type at this address |
| `BitOffset` | int | Bit-level offset (`Byte × 8 + Bit`) |
| `BlockNumber` | int | DB number (for DB access only) |
| `Informative` | bool | If `true`, import ignores this value |

**Area values:**

| Area | Meaning | TIA prefix |
| --- | --- | --- |
| `Input` | Process image input | `%I` |
| `Output` | Process image output | `%Q` |
| `Memory` | Merker/flag memory | `%M` |
| `PeripheryInput` | Direct periphery input (bypasses process image) | `%PI` |
| `PeripheryOutput` | Direct periphery output | `%PQ` |
| `DB` | Data block (partly qualified, DB register) | `DB` |
| `DI` | Instance data block (partly qualified, DI register) | `DI` |
| `FB` | Function block area | — |
| `FC` | Function area | — |
| `Timer` | S7 timer | `T` |
| `Counter` | S7 counter | `C` |
| `Local` | Classic local stack (S7-300/400) | `L` |
| `None` | Unspecified | — |

For `DB` access, `BlockNumber` holds the DB number and `BitOffset` addresses
within that DB.

#### Constant Attributes

The `Constant` element can carry additional attributes beyond its `ConstantType`
and `ConstantValue` children:

```xml
<Constant Name="PI" Scope="GlobalConstant">
  <ConstantType>Real</ConstantType>
  <ConstantValue>3.14159</ConstantValue>
  <StringAttribute Name="Format" Informative="true">Real</StringAttribute>
</Constant>
```

| Attribute/Element | Meaning |
| --- | --- |
| `Name` | Optional symbolic name |
| `Scope` | Optional scope qualifier |
| `ConstantType` | Data type (may be `Informative`) |
| `ConstantValue` | The constant value (may be `Informative`) |
| `StringAttribute` (Format) | Display format (informative) |
| `StringAttribute` (FormatFlags) | Format flags (informative) |
| `BooleanAttribute` | Up to 2 boolean metadata attributes |

**Format values** for the display format:

`Real`, `Bin`, `DecSigned`, `DecUnsigned`, `Pointer`, `CharSequence`,
`DecSequence`, `Hex`, `S5Count`, `Time`, `Date`, `TimeOfDay`, `S5Time`,
`Bool`, `Oct`, `Bcd`, `DateAndTime`, `String`, `Any`, `Number`, `Char`,
`HexSequence`

**FormatFlags** is a comma-separated combination of: `None`, `Lower`, `Format`,
`Size`, `Under`, `Exp`, `TypeQualifier`.

These format attributes are informative — they describe how TIA Portal displays
the constant, not the constant's intrinsic type.

#### Statusword Access (S7-300/400 Only)

For S7-300/400/WinAC controllers, Access nodes can reference CPU status word
bits:

```xml
<Access Scope="Statusword">
  <Statusword Combination="BR" />
</Access>
```

| Combination | Meaning |
| --- | --- |
| `BR` | Binary result |
| `OV` | Overflow |
| `OS` | Stored overflow |
| `EQ` | Equal to zero |
| `NE` | Not equal to zero |
| `GT` | Greater than zero |
| `LT` | Less than zero |
| `GE` | Greater or equal to zero |
| `LE` | Less or equal to zero |
| `UO` | Unordered (invalid real) |
| `NU` | Not unordered |
| `STW` | Entire status word |

These are not relevant for S7-1200/1500 controllers but may appear in legacy
project imports.

#### Indirect Addressing (STL Only)

STL blocks can use indirect addressing through address registers:

```xml
<Access Scope="...">
  <Indirect Width="Word" Area="Memory" Register="AR1" BitOffset="0" />
</Access>
```

| Attribute | Values | Meaning |
| --- | --- | --- |
| `Width` | `None`, `Bit`, `Byte`, `Word`, `Offset`, `Double`, `Pointer`, `Long`, `Any`, `Block` | Data width |
| `Area` | Same as Address Area enum | Memory area |
| `Register` | `AR1`, `AR2` | Address register used |
| `BitOffset` | int | Offset value |

#### Expression (SCL Only)

SCL expressions are composed of interleaved `Access` and `Token` elements:

```xml
<Access Scope="Expression">
  <Expression UId="50">
    <Access Scope="LocalVariable" UId="51">
      <Symbol><Component Name="counter" /></Symbol>
    </Access>
    <Token Text="+" />
    <Access Scope="LiteralConstant" UId="52">
      <Constant><ConstantValue>1</ConstantValue></Constant>
    </Access>
  </Expression>
</Access>
```

This represents `#counter + 1`. Expressions recursively contain Access nodes,
making them tree-structured.

#### Display Conventions for Variables

TIA Portal uses prefix conventions that are not stored in the XML:

| XML representation | TIA Portal display | Rule |
| --- | --- | --- |
| `LocalVariable` scope | `#variableName` | Prefix `#` for local/interface variables |
| `GlobalVariable` scope | `"DB_Name".field` | Quoted DB name for global access |
| `Address` scope | `%MW100` | `%` prefix with area letter and offset |
| `SliceAccessModifier="x0"` | `.%X0` | Prefix `%X` for bit slice |
| Multi-component symbol | `System.CLK100ms` | Dot-separated path |

A decoder rendering LAD-style output should apply these conventions.

### Part Nodes — Instructions and Operators

Part nodes represent instructions placed in the network:

```xml
<Part Name="Move" UId="41" DisabledENO="true">
  <TemplateValue Name="Card" Type="Cardinality">1</TemplateValue>
</Part>
```

#### Part Attributes

| Attribute | Meaning | Example |
| --- | --- | --- |
| `Name` | Instruction type | `Move`, `Contact`, `Add`, `TON` |
| `UId` | Unique identifier within compile unit | `41` |
| `DisabledENO` | Per-part ENO suppression | `true` |
| `Version` | Type version (system FBs/FCs) | `1.0` |

#### DisabledENO

When `DisabledENO="true"`, the instruction's ENO output is suppressed — the
part still has the `eno` pin in the wire graph but it will not propagate errors.
When absent or `false`, the ENO pin actively carries the execution result.

This is distinct from the block-level `SetENOAutomatically` attribute.

#### TemplateValue — Cardinality, Type, and Operation

`TemplateValue` elements control configurable properties of instructions:

```xml
<!-- Cardinality: controls pin count -->
<TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>

<!-- Type: explicit data type specification -->
<TemplateValue Name="SrcType" Type="Type">USInt</TemplateValue>
```

| Type attribute | Meaning | Names observed |
| --- | --- | --- |
| `Cardinality` | Number of expandable pins | `Card` |
| `Type` | Data type for typed instructions | `SrcType`, `DestType`, `time_type`, `date_type` |
| `Operation` | Configurable operation selection | (instruction-specific) |

`Operation` is a third template type defined in the schema. It allows certain
instructions to select between different operations (e.g. a configurable
math/logic box).

Cardinality controls how many pins an instruction exposes. Move with `Card=1`
has `out1`. Add with `Card=2` has `in1`, `in2`. An OR with `Card=3` has `in1`,
`in2`, `in3`. The yellow asterisk (`*`) shown in TIA Portal on instruction
boxes is the UI handle for expanding cardinality (adding more pins).

#### AutomaticTyped — Inferred Type

When a type is auto-resolved from connected operands instead of being
explicitly set:

```xml
<AutomaticTyped Name="SrcType" />
```

This is the auto-inferred alternative to `TemplateValue Type="Type"`. Same
`Name` attribute (e.g. `SrcType`), but the runtime resolves the type from
connected operands. Displayed as "Auto (Int)" or similar in TIA Portal.

#### Negated — Pin Inversion

A `Negated` child element marks which pin is inverted:

```xml
<Part Name="Contact" UId="27">
  <Negated Name="operand" />
</Part>
```

The `Name` attribute identifies the negated pin. For contacts, this produces a
NOT contact (slash symbol in LAD). Expected to appear on coils for negated
writes as well.

#### Invisible — Hidden Pins

A Part can have `Invisible` child elements marking pins that exist in the data
model but are hidden in the visual display:

```xml
<Part Name="SomeInstruction" UId="50">
  <Invisible Name="hiddenPin" />
</Part>
```

The `Name` attribute identifies the invisible pin. A parser should be aware
that some pins may be structurally present (and even wired) but not displayed
in TIA Portal. This does not affect execution — it is a display-only property.

#### Equation — Calculate Box

The `Part` element can contain an `Equation` child, used exclusively for the
Calculate box instruction:

```xml
<Part Name="Calculate" UId="55">
  <Equation>out := in1 + in2 * in3</Equation>
</Part>
```

The `Equation` element holds a free-form math expression as a text string. This
is the only Part type that uses inline text rather than pin-based wiring for its
core logic. The result variables and input variables referenced in the equation
correspond to the part's wired pins.

#### Comment on Parts and Calls

Individual Parts and Calls can carry an inline `Comment` element:

```xml
<Part Name="Move" UId="41">
  <Comment>
    <MultiLanguageText Lang="en-US">Copy sensor value</MultiLanguageText>
  </Comment>
</Part>
```

This is separate from the network-level multilingual `Title` and `Comment`
objects stored in `ObjectList`. Part-level comments annotate individual
instructions within a network.

#### Instance — System FB Instance Data

System FBs (TON, TOF, CTU, etc.) reference their backing static variable
through an `Instance` child:

```xml
<Part Name="TON" Version="1.0" UId="37">
  <Instance Scope="LocalVariable" UId="38">
    <Component Name="Timer" />
  </Instance>
  <TemplateValue Name="time_type" Type="Type">Time</TemplateValue>
</Part>
```

The `Instance` element links the instruction to a Static section member (here
`Timer` of type `IEC_TIMER` or `TON_TIME`) that stores the internal state across
calls. The `Version` on the Part matches the member's version.

The `Instance` element uses the same `Component` structure as `Symbol` in
Access nodes, and has its own `Scope` attribute (typically `LocalVariable`).

### Call Nodes — User Block Calls

User-defined blocks called inside a network use the `Call`/`CallInfo` pattern:

```xml
<Call UId="27">
  <CallInfo Name="deviceState" BlockType="FC">
    <Parameter Name="Alarm" Section="Input" Type="Bool" />
    <Parameter Name="Warning" Section="Input" Type="Bool" />
    <Parameter Name="deviceIcon" Section="InOut" Type="Int" />
  </CallInfo>
</Call>
```

| XML | Meaning |
| --- | --- |
| `Call UId="27"` | One call instance in the current network |
| `CallInfo Name="deviceState"` | Called block/function name |
| `BlockType="FC"` | Called block kind (`FC`, `FB`) |
| `Parameter` | Visible call pin/parameter |
| `Section="Input"` | Input parameter |
| `Section="InOut"` | In/out parameter |
| `Section="Return"` | Function return value |

Call pins use **parameter names** (e.g. `Alarm`, `Warning`, `deviceIcon`)
rather than generic pin names (`in1`, `in2`). The `en` and `eno` pins are
implicit and use those exact names.

#### Parameter Attributes

The `Parameter` element has additional attributes from the schema:

| Attribute | Meaning |
| --- | --- |
| `Name` | Parameter name |
| `Section` | Section in the called block's interface (`Input`, `Output`, `InOut`, `Return`) |
| `Type` | Data type |
| `TemplateReference` | Reference to a template type parameter |
| `Informative` | If `true`, import ignores this parameter entry |

Parameters may also carry `InterfaceFlags` via a `StringAttribute`: values are
`None`, `Mandatory`, `S7_Visible`, or a comma-separated combination.

#### CallInfo Metadata

`CallInfo` can carry informative metadata:

| Child element | Meaning |
| --- | --- |
| `IntegerAttribute` (BlockNumber) | The called block's number (informative) |
| `DateAttribute` (ParameterModifiedTS) | Parameter modification timestamp (informative) |
| `Instance` | Instance data reference (for FB calls) |
| `NamelessParameter` | Positional parameters without names |

### System Functions vs User Block Calls

System functions and user-defined blocks use different XML representations:

| Representation | Used for | Parameter discovery |
| --- | --- | --- |
| `Part` (with `Version`) | System FCs (`RD_LOC_T`, `SCALE_R`) | Pin names from instruction library |
| `Part` (with `Instance` + `Version`) | System FBs (`TON`, `TOF`, `CTU`) | Instance child references static variable |
| `Call`/`CallInfo` | User-defined FC/FB calls | Self-documenting: `CallInfo` lists all parameters |

For `Part`-based system functions, the parser must know valid pin names from an
instruction catalog. For `Call`-based user blocks, the parameter list is embedded
in the XML.

Note: Some system functions may appear as `Call`/`CallInfo` in certain exports
(observed with `SCALE_R` and `DPRD_DAT`). The `Part` vs `Call` distinction
for system functions may vary by TIA version or function category. A robust
parser should handle both representations.

#### Call Wiring

Arguments are connected through `Wire` elements, not stored inside `Parameter`
entries:

```xml
<Wire UId="29">
  <IdentCon UId="21" />
  <NameCon UId="27" Name="Alarm" />
</Wire>
```

Read as: access node 21 is connected to parameter `Alarm` on call node 27.

Resolution flow:

1. Index every `Call`, `Part`, and `Access` by `UId`.
2. Read `CallInfo` to get the called block name, block type, and parameter list.
3. For every `Wire`, find `NameCon` entries where `UId` matches the call id.
4. Use the `NameCon Name` value as the called parameter name.
5. Resolve the other wire endpoint (`IdentCon`) back to an `Access`.
6. Render the call with connected inputs, outputs, returns, and EN/ENO pins.

## Wire Topology

Wires connect Parts, Access nodes, and power sources. A `Wire` element contains
two or more endpoints.

### Wire Endpoint Types

Six endpoint types exist:

| Endpoint | Meaning | Example |
| --- | --- | --- |
| `Powerrail` | Left power rail, always TRUE | `<Powerrail />` |
| `IdentCon` | Reference to an Access node (data) | `<IdentCon UId="21" />` |
| `NameCon` | Reference to a Part/Call pin by name | `<NameCon UId="41" Name="en" />` |
| `OpenCon` | Explicitly unused output | `<OpenCon UId="43" />` |
| `Openbranch` | Open/unterminated branch | `<Openbranch />` |
| (absent) | Implicitly unused output | No wire element for that pin |

### Powerrail

The `Powerrail` element represents the left power rail in LAD — unconditional
TRUE. It always appears as the first child of a `Wire`:

```xml
<Wire UId="52">
  <Powerrail />
  <NameCon UId="41" Name="en" />
  <NameCon UId="42" Name="in" />
</Wire>
```

### Wire Fan-Out

A single wire can connect one source to multiple sinks:

```xml
<Wire UId="52">
  <Powerrail />
  <NameCon UId="41" Name="en" />
  <NameCon UId="42" Name="in" />
  <NameCon UId="44" Name="in" />
  <NameCon UId="46" Name="in" />
</Wire>
```

The first child is the source, all subsequent children are sinks. Fan-out works
from any output — not just Powerrail. A contact's `out` pin or an instruction's
`eno` pin can also fan out to multiple destinations.

This is how LAD encodes:

- **Parallel rungs** — Powerrail fanning to multiple contact `in` pins
- **Branch points** — one contact's `out` feeding multiple comparison `pre` pins

### OpenCon — Explicitly Unused Output

An output pin can be explicitly marked as unused:

```xml
<Wire UId="58">
  <NameCon UId="37" Name="ET" />
  <OpenCon UId="43" />
</Wire>
```

This differs from an implicitly unused output (no wire at all). Both cases
mean "not connected". `OpenCon` typically appears when TIA Portal displays
the pin with a value but routes it nowhere (e.g. a timer's ET output showing
`T#0ms`).

A decoder should treat both absent wires and `OpenCon` wires as unconnected.

### Openbranch — Unterminated Branch

The `Openbranch` element represents an open, unterminated branch point in the
network:

```xml
<Wire UId="60">
  <NameCon UId="45" Name="out" />
  <Openbranch />
</Wire>
```

This is structurally similar to `OpenCon` but represents a branch that was
started but not completed in the visual editor. A decoder should treat it as
an unconnected endpoint.

### Execution Flow Reconstruction

The XML does not store explicit execution order, but it can be reconstructed:

1. **Parallel branches**: Powerrail wires list their `NameCon` sinks in
   top-to-bottom visual order. While not formally guaranteed, this ordering
   is consistent across all observed samples.

2. **Serial chains**: ENO/out chaining creates sequential dependencies. When
   `Part_A.eno → Part_B.en` or `Contact_A.out → Contact_B.in`, Part_B only
   executes if Part_A completed successfully.

3. **Topological sort**: For a decoder, start from Powerrail connections and
   follow wire endpoints, building a dependency graph. Topological sort
   produces a valid execution order.

## Part Pin Vocabulary

### Pin Pattern Taxonomy

Parts fall into categories based on their power flow pin naming:

| Category | Power-in pin | Power-out pin | Examples |
| --- | --- | --- | --- |
| Power flow parts | `in` | `out` | Contact, Coil, RCoil, SCoil (expected) |
| Comparison/edge contacts | `pre` | `out` | Lt, Eq, PContact |
| Box instructions | `en` | `eno` | Move, Add, Inc, TON, RD_LOC_T |
| Flip-flops | (multiple named inputs) | `q` | Rs |

The distinction matters: `pre`/`out` parts pass or block power flow based on
a condition (contact-like behavior), while `en`/`eno` parts perform operations
when enabled (box-like behavior).

### Complete Pin Reference

#### Contact — LAD Contact

```
Part Name="Contact"
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `in` | input | Power flow input (left side) |
| `operand` | input | Bool variable being tested |
| `out` | output | Power flow output (TRUE if contact closed) |

May have `Negated Name="operand"` child for NOT contacts.

#### Coil — LAD Output Coil

```
Part Name="Coil"
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `in` | input | Power flow input |
| `operand` | input | Bool variable being written |
| `out` | output | Power flow continuation |

Coils can be daisy-chained: `Coil_A.out → Coil_B.in`. Both coils write their
respective variables from the same rung result.

#### RCoil — Reset Coil

```
Part Name="RCoil"
```

Same pins as Coil. When powered, resets (writes FALSE to) its operand.
Displayed as `( R )` in TIA Portal.

#### SCoil — Set Coil (Expected)

```
Part Name="SCoil"
```

Not yet observed but expected by symmetry. When powered, sets (writes TRUE to)
its operand. Displayed as `( S )` in TIA Portal.

#### O — OR Junction

```
Part Name="O"
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `in1`…`inN` | input | Power flow inputs (one per branch) |
| `out` | output | OR result |

Cardinality controls input count. Merges parallel LAD branches.

#### Move — Move/Assign

```
Part Name="Move"
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `en` | input | Enable |
| `in` | input | Source value |
| `out1`…`outN` | output | Destination(s) |
| `eno` | output | Enable out |

Cardinality controls output count. The yellow asterisk (`*`) in TIA Portal is
the UI handle for adding more output pins.

#### Add — Addition

```
Part Name="Add"
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `en` | input | Enable |
| `in1`…`inN` | input | Operands |
| `out` | output | Result |
| `eno` | output | Enable out |

Cardinality controls input count.

#### Inc — Increment

```
Part Name="Inc"
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `en` | input | Enable |
| `operand` | input | Variable to increment in-place |
| `eno` | output | Enable out |

#### Lt, Eq (and expected Gt, Ge, Le, Ne) — Comparisons

```
Part Name="Lt"   (less than)
Part Name="Eq"   (equal)
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `pre` | input | Precondition / power flow input |
| `in1` | input | Left operand |
| `in2` | input | Right operand |
| `out` | output | Power flow output (TRUE if condition met) |

Uses `pre` instead of `en` — comparisons behave like contacts (pass/block
power flow) rather than box instructions. Type specified via
`TemplateValue Name="SrcType"`.

#### Rs — RS Flip-Flop (Reset Priority)

```
Part Name="Rs"
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `s1` | input | Set input |
| `r` | input | Reset input |
| `operand` | input | Memory variable (stored state) |
| `q` | output | Output state |

Reset-dominant. An `Sr` (set-priority) variant is expected.

#### PBox — P_TRIG Positive Edge Detection (Box Form)

```
Part Name="PBox"
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `in` | input | Power flow / signal input |
| `bit` | input | Edge memory variable |
| `out` | output | Power flow output (TRUE for one scan on rising edge) |

The power flow itself is the monitored signal. The `bit` pin stores the
previous scan state.

#### PContact — Positive Edge Detection (Contact Form)

```
Part Name="PContact"
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `pre` | input | Power flow precondition |
| `operand` | input | Signal to check for rising edge |
| `bit` | input | Edge memory variable |
| `out` | output | Power flow output (TRUE for one scan on rising edge) |

Key difference from PBox: PContact separates the monitored signal (`operand`)
from the power flow enable (`pre`). PBox uses the power flow as the signal.
Displayed as a contact with `P` marker in TIA Portal.

By symmetry, `NBox` and `NContact` are expected for negative (falling) edge
detection.

#### TON — Timer On-Delay (System FB)

```
Part Name="TON" Version="1.0"
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `IN` | input | Timer input (start timing when TRUE) |
| `PT` | input | Preset time |
| `Q` | output | Timer output (TRUE after PT elapsed) |
| `ET` | output | Elapsed time |

**Pin names are UPPERCASE** — unlike all other parts which use lowercase. This
reflects that TON is a system FB with formally defined IEC parameter names.

Has `Instance` child referencing the backing static variable. `TemplateValue
Name="time_type"` specifies the time datatype.

Other IEC timer variants expected: `TOF` (off-delay), `TP` (pulse).

#### RD_LOC_T — Read Local Time (System FC)

```
Part Name="RD_LOC_T" Version="1.0"
```

| Pin | Direction | Meaning |
| --- | --- | --- |
| `en` | input | Enable |
| `RET_VAL` | output | Return value (Word) |
| `OUT` | output | Time/date output |
| `eno` | output | Enable out |

System FC represented as a `Part` (not `Call`/`CallInfo`). `TemplateValue
Name="date_type"` specifies the output format: `DTL` or `LDT`.

Multiple instances can be ENO-chained: `RD_LOC_T(DTL).eno → RD_LOC_T(LDT).en`.

## NetworkSource — SCL/Structured Text Networks

When a compile unit uses SCL, `NetworkSource` contains `StructuredText` instead
of `FlgNet`:

```xml
<NetworkSource>
  <StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4">
    ...
  </StructuredText>
</NetworkSource>
<ProgrammingLanguage>SCL</ProgrammingLanguage>
```

`StructuredText` uses a different namespace (`/StructuredText/v4`) from FlgNet
(`/FlgNet/v5`).

### SCL Is a Tokenized AST

The schema reveals that `StructuredText` is **not raw text** — it is a fully
tokenized abstract syntax tree. The container holds an interleaved sequence of:

| Element | Meaning |
| --- | --- |
| `Access` | Variable reference, constant, expression, or call (same `Access` type as LAD/FBD) |
| `Token` | Keyword, operator, punctuation, or literal text fragment |
| `Parameter` | Named parameter in a function/FB call |
| `Text` | Free-form text content (used in comments) |
| `Comment` (via `Comment_G`) | Line comments, block comments |

Each SCL identifier, operator, keyword, and value is a separate XML element.
The SCL source is reconstructed by concatenating these elements in sequence.

```xml
<StructuredText xmlns="...">
  <Access Scope="LocalVariable" UId="21">
    <Symbol><Component Name="result" /></Symbol>
  </Access>
  <Token Text=" := " />
  <Access Scope="LocalVariable" UId="22">
    <Symbol><Component Name="input1" /></Symbol>
  </Access>
  <Token Text=" + " />
  <Access Scope="LiteralConstant" UId="23">
    <Constant><ConstantValue>1</ConstantValue></Constant>
  </Access>
  <Token Text=";" />
  <LineComment Inserted="true" NoClosingBracket="true" UId="24">
    <Text UId="25">  increment the value</Text>
  </LineComment>
</StructuredText>
```

This represents `#result := #input1 + 1; // increment the value`.

SCL-specific Access scopes like `Expression`, `PredefinedVariable` (`ENO`),
`Instruction`, `Call`, and `Label` appear only within `StructuredText`.

### SCL Comment Elements

| Element | Attributes | Meaning |
| --- | --- | --- |
| `LineComment` | `Inserted`, `NoClosingBracket` | SCL comment block |
| `Text` | `UId` | Comment text content |

`Inserted="true"` means user-inserted (not auto-generated).
`NoClosingBracket="true"` means no closing `*)` bracket needed.

### Mixed Language Blocks

A block with `ProgrammingLanguage="LAD"` at the block level can contain
individual SCL networks. Always check the per-compile-unit
`ProgrammingLanguage`.

## NetworkSource — STL/Statement List Networks

When a compile unit uses STL, `NetworkSource` contains `StatementList`:

```xml
<NetworkSource>
  <StatementList xmlns="...">
    <StlStatement UId="21">
      <StlToken Text="A" />
      <Access Scope="LocalVariable" UId="22">
        <Symbol><Component Name="Start" /></Symbol>
      </Access>
    </StlStatement>
    <StlStatement UId="23">
      <StlToken Text="Assign" />
      <Access Scope="LocalVariable" UId="24">
        <Symbol><Component Name="Motor" /></Symbol>
      </Access>
    </StlStatement>
  </StatementList>
</NetworkSource>
```

This represents:

```
A     #Start
=     #Motor
```

### STL Statement Structure

Each `StlStatement` contains:

| Element | Meaning |
| --- | --- |
| `Comment` (via `Comment_G`) | Optional leading comment |
| `LabelDeclaration` | Optional jump label |
| `StlToken` | The instruction mnemonic (required except for empty lines) |
| `Access` | Optional operand |

The `StlToken` element has a `Text` attribute holding the instruction name from
a fixed enumeration. It may also contain child elements: an `IntegerAttribute`
for `NumBLs` (informative blank line count) and an optional `Token` child for
multi-word instructions (e.g. `NOP 0` → StlToken `Text="NOP_0"`, or
instructions separated by comments).

### STL Instruction Reference

The schema defines the complete STL instruction set. Instructions are grouped
by category:

**Bit logic:**
`A` (AND), `AN` (AND NOT), `O` (OR), `ON` (OR NOT), `X` (XOR), `XN` (XOR NOT),
`A_BRACK`, `AN_BRACK`, `O_BRACK`, `ON_BRACK`, `X_BRACK`, `XN_BRACK` (bracket
variants), `BRACKET` (closing bracket `)`), `S` (set), `R` (reset),
`Assign` (`=`), `Rise` (positive edge), `Fall` (negative edge), `SET`,
`CLR`, `NEG`, `SAVE`

**Load/Transfer:**
`L` (load), `T` (transfer)

**Timer/Counter:**
`OnDelay` (SD/SE), `OffDelay` (SF/SA), `Pulse` (SP/SI), `Extend` (SE/SV),
`Retentive` (SS), `Free`, `LC`,
`CU` (count up), `CD` (count down)

**Comparison:**
`LT_I`, `LE_I`, `EQ_I`, `GE_I`, `GT_I`, `NE_I` (integer),
`LT_R`, `LE_R`, `EQ_R`, `GE_R`, `GT_R`, `NE_R` (real),
`LT_D`, `LE_D`, `EQ_D`, `GE_D`, `GT_D`, `NE_D` (double integer)

**Math (integer):**
`ADD_I` (+F), `SUB_I` (-F), `MUL_I` (×F), `DIV_I` (:F),
`ADD_D` (+D), `SUB_D` (-D), `MUL_D` (×D), `DIV_D` (:D), `MOD_D`

**Math (real):**
`ADD_R` (+G), `SUB_R` (-G), `MUL_R` (×G), `DIV_R` (:G)

**Math (special):**
`SQRT`, `SQR`, `LN`, `EXP`, `SIN`, `ASIN`, `COS`, `ACOS`, `TAN`, `ATAN`,
`ABS_R`, `NEG_R`, `NEG_I`, `NEG_D`, `INV_I`, `INV_D`

**Shift/Rotate:**
`SLD`, `SLW`, `SRD`, `SRW`, `SRSD` (SSD), `SRSW` (SSW),
`RLD`, `RRD`, `RLDA`, `RRDA`

**Conversion:**
`BTI` (BCD→Int), `ITB` (Int→BCD), `BTD` (BCD→DInt), `DTB` (DInt→BCD),
`DTR` (DInt→Real), `ITD` (Int→DInt),
`RND`, `RND_M`, `RND_P`, `TRUNC`,
`CAW` (swap word bytes), `CAD` (swap dword bytes)

**Jump:**
`JU` (unconditional), `JC` (conditional), `JCN`, `JCB`, `JNB`,
`JO`, `JOS`, `JUN`, `JZ`, `JP`, `JM`, `JN`, `JMZ`, `JPZ`,
`JBI`, `JNBI`, `JL`, `LOOP`

**Block calls:**
`CALL`, `CC` (conditional call), `UC` (unconditional call),
`OPEN_DB` (AUF), `OPEN_DI` (AUF DI)

**Address register:**
`LAR1`, `LAR2`, `TAR1`, `TAR2`, `LAR1_ACCU1`, `LAR1_AR2`, `LAR2_ACCU1`,
`TAR1_ACCU1`, `TAR2_ACCU1`, `TAR1_AR2`, `ADDAR1`, `ADDAR2`, `CAR` (TAR)

**Word logic:**
`AW`, `OW`, `XW`, `AD`, `OD`, `XD`

**Accumulator:**
`CAC` (TAK — swap ACCU1/ACCU2), `LEAVE`, `PUSH`, `POP`, `ENT`,
`ADD` (add constant to ACCU1)

**Data block:**
`CDB` (TDB — swap DB/DI), `L_DBLG`, `L_DILG`, `L_DBNO`, `L_DINO`

**Block end:**
`BE` (BEA — block end unconditional), `BEC` (BEB — block end conditional),
`BEU` (block end unconditional)

**Special:**
`MOVE`, `MOVE_BLOCK`, `BLD` (image builder),
`MCR_BRACK`, `BRACK_MCR`, `MCRA`, `MCRD`,
`NOP_0`, `NOP_1`, `INC`, `DEC`,
`COMMENT`, `EMPTY_LINE`, `PSEUDO`

The German STEP 7 mnemonics are given in parentheses where applicable. STL is
primarily a S7-300/400 language; S7-1200/1500 do not support STL natively.

## NetworkSource — GRAPH/SFC Networks

When a compile unit uses GRAPH, `NetworkSource` contains the `Graph` container
representing a Sequential Function Chart (SFC):

```xml
<NetworkSource>
  <Graph>
    <PreOperations> ... </PreOperations>
    <Sequence> ... </Sequence>
    <PostOperations> ... </PostOperations>
    <AlarmsSettings> ... </AlarmsSettings>
  </Graph>
</NetworkSource>
```

### GRAPH Top-Level Structure

| Element | Meaning |
| --- | --- |
| `PreOperations` | Permanent operations executed before the sequence (every scan) |
| `Sequence` | The step/transition sequence (one or more per Graph) |
| `PostOperations` | Permanent operations executed after the sequence (every scan) |
| `AlarmsSettings` | Alarm category configuration for the sequencer |

`PreOperations` and `PostOperations` share the same type: each can contain a
`Title`, `Comment`, and multiple `PermanentOperation` elements. Each
`PermanentOperation` has its own `ProgrammingLanguage` and contains a `FlgNet`
(reusing the same LAD/FBD graph structure).

### Sequence

A `Sequence` contains the core SFC elements:

```xml
<Sequence>
  <Title> ... </Title>
  <Comment> ... </Comment>
  <Steps>
    <Step Number="1" Name="S1" Init="true" MaximumStepTime="T#30s" WarningTime="T#25s">
      <Actions> ... </Actions>
      <Supervisions> ... </Supervisions>
      <Interlocks> ... </Interlocks>
    </Step>
  </Steps>
  <Transitions> ... </Transitions>
  <Branches> ... </Branches>
  <Connections> ... </Connections>
</Sequence>
```

### Steps

Each `Step` has:

| Attribute | Type | Meaning |
| --- | --- | --- |
| `Number` | int | Step number (required) |
| `Name` | string | Step name (required) |
| `Init` | bool | `true` for the initial step (default `false`) |
| `IsMissing` | bool | Marks a placeholder for a deleted step (default `false`) |
| `MaximumStepTime` | string | Maximum allowed time in step (e.g. `T#30s`) |
| `WarningTime` | string | Warning time threshold |

Steps contain three sub-elements:

**Actions** — operations performed while the step is active. Each `Action` has:

| Attribute | Meaning |
| --- | --- |
| `Qualifier` | IEC 61131-3 action qualifier (see table below) |
| `Event` | Event trigger (see table below) |
| `Interlock` | Whether this action is interlock-dependent |

Action content is a sequence of `Access` and `Token` elements (like SCL).

**Action Qualifiers (IEC 61131-3):**

| Qualifier | Meaning |
| --- | --- |
| `N` | Non-stored (active while step is active) |
| `S` | Set (stored, stays active until reset) |
| `R` | Reset (clears a previously set action) |
| `D` | Delayed (activated after a delay) |
| `L` | Time limited (active for a limited time) |
| `ON` | On-delay with set |
| `OFF` | Off-delay with reset |
| `CD` | Count down |
| `CR` | Count reset |
| `CS` | Count set |
| `CU` | Count up |
| `TD` | Time delayed |
| `TF` | Time finished |
| `TL` | Time limited |
| `TR` | Time reset |

**Action Events:**

| Event | Meaning |
| --- | --- |
| `A1` | On activation |
| `S0`, `S1` | Step deactivation / activation |
| `L0`, `L1` | On leaving / entering |
| `R1` | On reaching |
| `V0`, `V1` | Additional event triggers |

**Supervisions** — monitoring conditions checked while the step is active. Each
`Supervision` contains a `Title`, an optional `AlarmText` (multilingual), and
a `FlgNet` with the monitoring logic in LAD/FBD.

**Interlocks** — safety conditions that must be satisfied for actions to execute.
Each `Interlock` also contains a `Title`, optional `AlarmText`, and a `FlgNet`.

Step names and transition names support multilingual text through `StepName`
and `TransitionName` elements containing `MultiLanguageText` children.

### Transitions

Each `Transition` defines the condition for moving from one step to the next:

```xml
<Transition Number="1" Name="T1" ProgrammingLanguage="LAD" IsMissing="false">
  <TransitionName>
    <MultiLanguageText Lang="en-US">Check sensor</MultiLanguageText>
  </TransitionName>
  <Comment> ... </Comment>
  <FlgNet> ... </FlgNet>
</Transition>
```

The transition condition is expressed as a `FlgNet` using the same LAD/FBD
graph structure as regular networks. Each transition has its own
`ProgrammingLanguage` — transitions within the same sequence can use different
languages.

### Branches

Branches define parallel and alternative paths:

```xml
<Branch Number="1" Type="SimBegin" Cardinality="3" />
<Branch Number="2" Type="SimEnd" Cardinality="3" />
<Branch Number="3" Type="AltBegin" Cardinality="2" />
<Branch Number="4" Type="AltEnd" Cardinality="2" />
```

| Branch Type | Meaning |
| --- | --- |
| `SimBegin` | Start of simultaneous (parallel) branch |
| `SimEnd` | End of simultaneous branch (synchronization) |
| `AltBegin` | Start of alternative (selection) branch |
| `AltEnd` | End of alternative branch (merge) |

`Cardinality` indicates how many branches diverge or converge.

### Connections

Connections define the flow between steps, transitions, and branches:

```xml
<Connection>
  <NodeFrom><StepRef Number="1" /></NodeFrom>
  <NodeTo><TransitionRef Number="1" /></NodeTo>
  <LinkType>Direct</LinkType>
</Connection>
```

Node references can be:

| Element | Meaning |
| --- | --- |
| `StepRef` | Reference to a step by number |
| `TransitionRef` | Reference to a transition by number |
| `BranchRef` | Reference to a branch by number (has `In`/`Out` attributes) |
| `EndConnection` | End of a connection chain |

Link types:

| Value | Meaning |
| --- | --- |
| `Direct` | Normal sequential flow |
| `Jump` | Jump to a non-adjacent step (drawn as an arrow in TIA Portal) |

### Alarm Settings

The `AlarmsSettings` element configures alarm categories for the GRAPH block:

```xml
<AlarmsSettings>
  <AlarmSupervisionCategories>
    <AlarmSupervisionCategory Id="1" DisplayClass="5" />
  </AlarmSupervisionCategories>
  <AlarmInterlockCategory Id="2" />
  <AlarmCategorySupervision Id="3" />
  <AlarmWarningCategory Id="4" />
  ...
</AlarmsSettings>
```

Categories are divided into three groups (Interlock, Supervision, Warning), each
with a main category and two subcategories. `AlarmSupervisionCategory` also has
a `DisplayClass` (0–16) controlling how the alarm is displayed.

## Supervision System

Three schema files define a supervision/alarm configuration system used with
GRAPH blocks and technology objects. These describe monitored operands, alarm
conditions, and instance-level linking.

### Block/Type-Level Supervisions

Both `BlockTypeSupervisions` and `PLCDataTypeSupervisions` share the same
structure (at block level and UDT level respectively):

```xml
<BlockTypeSupervision Number="1" Type="Operand">
  <SupervisedOperand Name="Position_actual" />
  <SupervisionName>Position fault</SupervisionName>
  <SupervisedStatus>true</SupervisedStatus>
  <DelayOperand Name="Delay_pos" />
  <Conditions>
    <Condition>
      <ConditionOperand Number="1" Name="Position_target" />
      <TriggeringStatus>true</TriggeringStatus>
    </Condition>
  </Conditions>
  <CategoryNumber>3</CategoryNumber>
  <SubCategory1Number>1</SubCategory1Number>
  <SubCategory2Number>0</SubCategory2Number>
  <SpecificField>
    <AssociatedValues>
      <AssociatedValue>
        <AssociatedValueOperand Number="1" Name="Speed" />
      </AssociatedValue>
    </AssociatedValues>
    <SpecificFieldText>
      <MultiLanguageText Lang="en-US">Position error</MultiLanguageText>
    </SpecificFieldText>
  </SpecificField>
</BlockTypeSupervision>
```

| Element | Meaning |
| --- | --- |
| `SupervisedOperand` | The variable being monitored |
| `SupervisionName` | Human-readable name for this supervision |
| `SupervisedStatus` | Whether the supervision is active (`true`/`false`) |
| `DelayOperand` | Variable providing a delay time |
| `Conditions` | Up to 3 conditions, each with a `ConditionOperand` and `TriggeringStatus` |
| `CategoryNumber` | Alarm category (1–8) |
| `SubCategory1Number` | First subcategory |
| `SubCategory2Number` | Second subcategory |
| `SpecificField` | Additional info: up to 3 `AssociatedValue` operands and multilingual text |

Supervision types:

| Type | Meaning |
| --- | --- |
| `Action` | Action monitoring |
| `Interlock` | Interlock condition |
| `Operand` | Operand value monitoring |
| `Position` | Position monitoring |
| `Reaction` | Reaction to a condition |
| `MessageText` | Alarm message text |
| `MessageError` | Error message |

### Instance-Level Supervisions

`InstSupervisions` links supervision definitions to specific block instances:

```xml
<InstSupervision>
  <InstancePath>MyFB_1</InstancePath>
  <SupervisedOperand>Position_actual</SupervisedOperand>
  <SupervisionName>Position fault</SupervisionName>
  <InstanceLabel>Station_1</InstanceLabel>
  <StateStructName>StateStruct</StateStructName>
  <SupervisionId>1</SupervisionId>
</InstSupervision>
```

| Element | Meaning |
| --- | --- |
| `InstancePath` | Path to the FB instance |
| `SupervisedOperand` | Monitored operand name |
| `SupervisionName` | Supervision name |
| `InstanceLabel` | Human-readable instance label |
| `StateStructName` | Name of the state structure (optional) |
| `SupervisionId` | Supervision identifier (optional) |

Up to 10,000 instance supervisions can be defined per block.

## Multilingual Text

Network titles and comments are stored as multilingual text objects, attached
to blocks and compile units through `ObjectList` entries.

### Block-Level Text

Blocks have a `Title` and `Comment` under their `ObjectList`:

```xml
<MultilingualText ID="8" CompositionName="Title">
  <ObjectList>
    <MultilingualTextItem ID="9" CompositionName="Items">
      <AttributeList>
        <Culture>en-US</Culture>
        <Text />
      </AttributeList>
    </MultilingualTextItem>
  </ObjectList>
</MultilingualText>
```

### Network-Level Text

Each compile unit has its own `Title` and `Comment`:

```xml
<MultilingualText ID="6" CompositionName="Title">
  <ObjectList>
    <MultilingualTextItem ID="7" CompositionName="Items">
      <AttributeList>
        <Culture>en-US</Culture>
        <Text>OUTPUT</Text>
      </AttributeList>
    </MultilingualTextItem>
  </ObjectList>
</MultilingualText>
```

Titles appear in TIA Portal as `Network 1: OUTPUT`. Empty `<Text />` elements
are valid.

## Stable Parser Model

A practical reader can map the XML into this model:

```text
SimaticMlDocument
  EngineeringVersion
  Block
    Kind                    (FC, FB, OB, DB)
    Id
    Name
    Number
    ProgrammingLanguage
    MemoryLayout
    MemoryReserve           (FB only)
    SetEnoAutomatically
    Title                   (multilingual)
    Comment                 (multilingual)
    Interface
      Sections[]
        Name
        Members[]
          Name
          Datatype          (may be UDT reference in quotes)
          Accessibility
          Remanence
          Version
          StartValue
          Comment
          BooleanAttributes[]
    Networks[]
      ProgrammingLanguage   (may differ from block language)
      Title                 (multilingual)
      Comment               (multilingual)
      -- If LAD/FBD (FlgNet):
      Labels[]
        Name
        Comment
      Accesses[]
        UId
        Scope               (20 values, see Access Scope table)
        -- scope-dependent child:
        Symbol              (Component chain, may have SliceAccessModifier,
                             AccessModifier, SimpleAccessModifier)
        Constant            (ConstantType, ConstantValue, Name, Format)
        Address             (Area, Type, BitOffset, BlockNumber)
        Expression          (recursive Access + Token)
        CallInfo            (SCL/STL only)
        Instruction         (SCL/STL only)
        Label               (SCL/STL only)
        Indirect            (STL only, Width, Area, Register)
        Statusword          (S7-300/400 only, Combination)
        PredefinedVariable  (SCL only, currently ENO)
        DataType            (type references)
        Reference           (reference-type access)
      Parts[]
        UId
        Name
        DisabledENO
        Version
        TemplateValues[]    (Type: Cardinality, Type, Operation)
        AutomaticTyped[]
        Negated[]
        Invisible[]
        Equation            (Calculate box only)
        Comment             (inline part comment)
        Instance            (for system FBs)
      Calls[]
        UId
        CallInfo
          Name
          BlockType         (FC, FB, OB, DB, UDT, FBT, FCT)
          Instance
          Parameters[]
            Name
            Section
            Type
            TemplateReference
            Informative
      Wires[]
        UId
        Endpoints[]         (Powerrail, IdentCon, NameCon, OpenCon,
                             Openbranch)
      -- If SCL (StructuredText):
      TokenizedContent[]    (interleaved Access, Token, Parameter,
                             Text, Comment elements — tokenized AST)
      -- If STL (StatementList):
      Statements[]
        Comment
        LabelDeclaration
        StlToken            (instruction mnemonic from STL_TE)
        Access              (operand)
      -- If GRAPH (Graph):
      PreOperations[]       (PermanentOperation with FlgNet)
      Sequences[]
        Steps[]
          Number, Name, Init, MaximumStepTime, WarningTime
          Actions[]         (Qualifier, Event, Interlock)
          Supervisions[]    (FlgNet + AlarmText)
          Interlocks[]      (FlgNet + AlarmText)
        Transitions[]
          Number, Name, ProgrammingLanguage
          FlgNet            (transition condition logic)
        Branches[]
          Number, Type      (SimBegin/SimEnd/AltBegin/AltEnd)
          Cardinality
        Connections[]
          NodeFrom          (StepRef/TransitionRef/BranchRef/EndConnection)
          NodeTo
          LinkType          (Direct/Jump)
      PostOperations[]      (PermanentOperation with FlgNet)
      AlarmsSettings
    Supervisions            (optional, for GRAPH/technology blocks)
      BlockTypeSupervisions[]
      PLCDataTypeSupervisions[]
      InstSupervisions[]
```

Keep the original XML IDs and UIds in the model. They are needed to reconnect
network graph elements and to round-trip or debug parser behavior.

## Decoder Rules

- Parse XML with namespace awareness.
- Use local-name matching for mixed namespaces.
- Treat block metadata and compile-unit metadata separately.
- Treat `Interface` as declarations, not as a simple property.
- Treat `NetworkSource` as a graph (FlgNet), tokenized AST (StructuredText),
  statement list (StatementList), or sequencer (Graph) depending on the
  programming language.
- Check per-compile-unit `ProgrammingLanguage` — do not assume all networks
  match the block language.
- Scope UId lookup tables per compile unit.
- Resolve `UId` references before trying to render LAD.
- Handle all six wire endpoint types: `Powerrail`, `IdentCon`, `NameCon`,
  `OpenCon`, `Openbranch`, and implicit absence.
- Do not deduplicate Access nodes — each variable use is a separate node.
- Recognize `&quot;...&quot;` datatype patterns as UDT references.
- Apply display conventions (`#` prefix, `%X` bit slice, `%I`/`%Q`/`%M`
  area prefix) when rendering.
- Handle `Informative` attributes — read them but do not rely on them for
  import correctness.
- Support all 20 Access scopes, even if the parser only fully processes a
  subset.
- Handle Component `AccessModifier="Array"` with nested Access children for
  array subscript indexing.
- Handle Component `SimpleAccessModifier` for periphery and quality info access.
- Preserve unknown attributes and elements for forward compatibility.
- Expect different block kinds to add or omit interface sections.
- Expect TIA version changes to add fields without changing the whole shape.

## What The Current Samples Confirm

- `Engineering version="V21"` identifies the TIA export version.
- Both `SW.Blocks.FC` and `SW.Blocks.FB` are observed with their structural
  differences (Static section, MemoryReserve, Return section handling).
- `Interface` contains inputs, outputs, in/out parameters, statics, temps,
  constants, and returns with varying attributes per block kind.
- Members can have `StartValue`, `Remanence`, `Version`, and system
  `BooleanAttribute` metadata.
- UDT references use XML-escaped quoted datatype names.
- `ObjectList/SW.Blocks.CompileUnit` contains the executable networks.
- Network language is stored per compile unit and can differ from the block
  language (LAD block with SCL networks).
- Five wire endpoint types observed in samples: `Powerrail`, `IdentCon`,
  `NameCon`, `OpenCon`, and implicit absence. Schema confirms a sixth:
  `Openbranch`.
- Wire fan-out connects one source to multiple sinks.
- UIds are scoped per compile unit, not per block.
- Access nodes are per-use, not per-variable.
- Multi-component symbols represent UDT/struct field access.
- `SliceAccessModifier` enables bit-level access on Byte/Word variables.
  Schema confirms full pattern: `[xbwdXBWD]\d+` for bit/byte/word/dword slices.
- Part pin names follow a consistent taxonomy: `in`/`out` for power flow,
  `pre`/`out` for comparisons/edge contacts, `en`/`eno` for box instructions.
- `TemplateValue` controls cardinality (pin count), type specification, and
  operation selection.
- `Negated` inverts specific pins on contacts and coils.
- `Invisible` hides pins from the visual display.
- `Equation` enables free-form math expressions in the Calculate box.
- System FBs use `Instance` children; system FCs use `Part` with `Version`.
- User block calls use `Call`/`CallInfo` with self-documenting parameters.
- Empty `NetworkSource` represents blank networks.
- `DisabledENO` suppresses ENO propagation per instruction.
- Schema defines 20 Access scopes, 13 programming languages, 7 block types,
  13 address areas, and complete STL/GRAPH element sets.

## What The Schemas Define But Samples Have Not Yet Confirmed

The following features are defined in the XSD schemas but have not been observed
in the four sample block exports. They are structurally valid and should be
expected in real-world projects:

- Access scopes beyond `LocalVariable`/`LiteralConstant`: `GlobalVariable`,
  `GlobalConstant`, `Address`, `TypedConstant`, `AlarmConstant`, etc.
- `Address` element for absolute memory addressing
- Component `AccessModifier="Array"` with nested Access children
- Component `SimpleAccessModifier` for periphery/quality access
- `TemplateValue Type="Operation"`
- `Invisible` pins on Parts
- `Equation` element for Calculate box
- `Openbranch` wire endpoint
- `Labels` section in FlgNet
- Inline `Comment` on individual Parts and Calls
- Full SCL tokenized AST structure (samples only show comment elements)
- STL `StatementList` networks
- GRAPH/SFC `Graph` networks with steps, transitions, branches
- Supervision system (`BlockTypeSupervisions`, `PLCDataTypeSupervisions`,
  `InstSupervisions`)
- IEC language variants (`LAD_IEC`, `FBD_IEC`)
- `Statusword` access for S7-300/400
- `Indirect` addressing for STL
- `PredefinedVariable` access for SCL (ENO)
- DB-related programming languages (`DB`, `SDB`, `DB_CPU`, `FB_IDB`,
  `SFB_IDB`, `DT_DB`)
