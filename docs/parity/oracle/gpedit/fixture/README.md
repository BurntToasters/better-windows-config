# Oracle fixture templates

These templates are written by Better Windows Config (BWC). They are not Microsoft files. The oracle capture tool copies them into `C:\Windows\PolicyDefinitions`, drives the classic Local Group Policy Editor over every policy, and records the resulting `Registry.pol`, `gpt.ini`, and `comment.cmtx` bytes as goldens. The BWC parser tests and E2E tests load the same files (through `BWC_E2E_ADMX_ROOT`) so both sides see one template set.

Nothing on the system reads these registry keys. The good fixture writes only below `Software\Policies\BWC-Oracle`, with one exception that is described below.

## Files

| File | Namespace | Purpose |
|---|---|---|
| `BwcOracleBase.admx`, `en-US/BwcOracleBase.adml` | `BWC.Policies.OracleBase` (prefix `bwcbase`) | The top category "BWC Oracle" and three `supportedOn` definitions, the way `Windows.admx` does for Windows. |
| `BwcOracle.admx`, `en-US/BwcOracle.adml` | `BWC.Policies.Oracle` (prefix `bwcoracle`, uses `bwcbase`) | 70 policies and the subcategories. |
| `bad/<Case>/...` | `BWC.Policies.Bad.<Case>` | Eight broken templates for load-error tests. Each is loaded alone, never together with the good fixture. |

The good fixture is schema valid. It does not use any attribute that the classic schema rejects. The BWC parser must still not validate against the schema.

## Tree layout and what it is meant to show

K stands for `Software\Policies\BWC-Oracle` in the tables below.

- BWC Oracle holds one policy directly (`Pol_Top_DirectUnderRoot`) next to its subcategories.
- Branches with both Machine and User policies appear under both roots: Basics, Elements (with Booleans, Numbers, Texts, Choices, Lists).
- Branches with Machine policies only appear under Computer Configuration only: Shared keys, Sort cases, "Tools & Utilities" (a category name with a literal ampersand), Explain text, and Machine only (three levels deep: Machine only, Machine nested, Machine deep).
- User only has no policy of its own and holds User nested, which holds one User policy. It is meant to appear under User Configuration only, two levels deep, with a first level that contains no policy directly.
- "Intentionally empty" (`EmptyCategory`) has no policies of either class. The intent is that it appears under neither root. The oracle records whether classic shows it.

## Good policies

Columns: class (M machine, U user, B both), key, value name, elements (id and kind), coverage item, open questions served. Open questions are numbered as in `docs/parity/gpedit-inventory.md` section 4.

| Policy | Class | Key | Value name | Elements | Coverage | OQ |
|---|---|---|---|---|---|---|
| `Pol_Top_DirectUnderRoot` | U | K | `TopFlag` | none | policy directly under the top category, default 1 and 0 | |
| `Pol_Class_Machine` | M | K\Basics | `MachineFlag` | none | class Machine, value name without explicit values | |
| `Pol_Class_User` | U | K\Basics | `UserFlag` | none | class User, value name without explicit values | |
| `Pol_Class_Both` | B | K\Basics | `BothFlag` | none | class Both | |
| `Pol_Value_Decimal` | M | K\Basics | `ModeNumber` | none | enabledValue 5 and disabledValue 9 (decimal) | |
| `Pol_Value_String` | U | K\Basics | `ModeText` | none | enabledValue `on` and disabledValue `off` (string) | |
| `Pol_Value_EnabledOnly` | M | K\Basics | `EnabledOnly` | none | enabledValue only | OQ-03 |
| `Pol_Value_DisabledOnly` | M | K\Basics | `DisabledOnly` | none | disabledValue only | OQ-03 |
| `Pol_Value_NoValueName_ElementsOnly` | M | K\Basics | none | `OnlyText` text, `OnlyNumber` decimal | no value name, writes only elements | OQ-03 |
| `Pol_Value_EnabledListOnly` | M | K\Basics | none | none | enabledList of three items, defaultKey, one item key override | OQ-04 |
| `Pol_Value_DisabledListOnly` | M | K\Basics | none | none | disabledList of two items, defaultKey, one item key override | OQ-04 |
| `Pol_Value_BothLists` | M | K\Basics | `ListMaster` | none | value name with enabledList and disabledList, item key override | OQ-04 |
| `Pol_Element_KeyOverride` | M | K\Basics | `ElementKeyFlag` | `OverrideText` text, key K\Basics\ElementKey | element key differs from policy key | OQ-04 |
| `Pol_Outside_KeyOutsideRoots` | M | `Software\BWC-Oracle-Gpedit` | `OutsideFlag` | none | the one key outside the policy roots | OQ-09 |
| `Pol_Both_WithElements` | B | K\Basics | `BothElementFlag` | `BothNumber` decimal 0 to 99 | class Both with an element, `supportedOn` defined in the same file | |
| `Pol_Bool_TrueFalseValues` | M | K\Elements\Bool | none | `TrueFalseCheck` boolean, true 10, false 20 | boolean with trueValue and falseValue | |
| `Pol_Bool_StringValues` | U | K\Elements\Bool | none | `StringCheck` boolean, true `yes`, false `no` | boolean with string values, default checked | |
| `Pol_Bool_TrueFalseLists` | M | K\Elements\Bool | none | `ListCheck` boolean with valueName, trueList and falseList (classic requires valueName) | boolean with lists, item key override | OQ-04 |
| `Pol_Bool_NoFalseValue` | M | K\Elements\Bool | none | `NoFalseCheck` boolean, true 1 only | boolean with no falseValue | OQ-02 |
| `Pol_Bool_TwoChecksReverseOrder` | M | K\Elements\Bool\Order | none | `ZetaCheck` boolean, then `AlphaCheck` boolean | two values in one key, defined in reverse name order | OQ-01 |
| `Pol_Decimal_MinMax` | M | K\Elements\Number | none | `MinMaxNumber` decimal 10 to 500 | decimal with minValue and maxValue, spin step 5 | |
| `Pol_Decimal_Required` | U | K\Elements\Number | none | `RequiredNumber` decimal, required | decimal required, spin off | |
| `Pol_Decimal_Soft` | M | K\Elements\Number | none | `SoftNumber` decimal 1 to 100, soft | decimal soft | |
| `Pol_Decimal_StoreAsText` | M | K\Elements\Number | none | `TextNumber` decimal 0 to 65535, storeAsText | decimal storeAsText | |
| `Pol_LongDecimal_Basic` | M | K\Elements\Number | none | `LongNumber` longDecimal 0 to 9000000000000 | longDecimal, `longDecimalTextBox` | |
| `Pol_LongDecimal_RequiredSoftText` | U | K\Elements\Number | none | `LongTextNumber` longDecimal, required, soft, storeAsText | longDecimal with all flags | |
| `Pol_Text_Plain` | M | K\Elements\Text | none | `PlainText` text | text, `textBox` with defaultValue | |
| `Pol_Text_Expandable` | M | K\Elements\Text | none | `ExpandText` text, expandable | text expandable | |
| `Pol_Text_MaxLength` | U | K\Elements\Text | none | `ShortText` text, maxLength 12 | text with maxLength | |
| `Pol_Text_Required` | M | K\Elements\Text | none | `RequiredText` text, required | text required | |
| `Pol_Text_ComboBoxSuggestions` | M | K\Elements\Text | none | `ComboText` text | `comboBox` with default and four suggestions, sorted | |
| `Pol_Text_ComboBoxNoSortRequired` | M | K\Elements\Text | none | `ComboNoSortText` text, required | `comboBox` with noSort, required | |
| `Pol_MultiText_Basic` | M | K\Elements\Text | none | `MultiLines` multiText | multiText, `multiTextBox` | |
| `Pol_MultiText_Limits` | U | K\Elements\Text | none | `LimitedLines` multiText, required, maxLength 20, maxStrings 3 | multiText with limits | |
| `Pol_Enum_DecimalItems` | M | K\Elements\Choice | none | `DecimalChoice` enum, items 0 to 3 | enum with decimal values | |
| `Pol_Enum_StringItems` | U | K\Elements\Choice | none | `StringChoice` enum, items red, green, blue | enum with string values | |
| `Pol_Enum_ItemValueList` | M | K\Elements\Choice | none | `ProfileChoice` enum, items 1, 2, 3 | enum items with their own valueList, defaultKey, key override | OQ-04 |
| `Pol_Enum_Required` | M | K\Elements\Choice | none | `RequiredChoice` enum, required | enum required | |
| `Pol_Enum_NoSortDefaultItem` | M | K\Elements\Choice | none | `UnsortedChoice` enum | `dropdownList` with noSort and defaultItem 1 | |
| `Pol_List_Plain` | M | K\Lists | none | `PlainList` list, key K\Lists\Plain | list without any attribute | OQ-06 |
| `Pol_List_EmptyPrefix` | M | K\Lists | none | `EmptyPrefixList` list, valuePrefix empty | list with an empty valuePrefix | OQ-06 |
| `Pol_List_Additive` | M | K\Lists | none | `AdditiveList` list, additive | list additive | OQ-06 |
| `Pol_List_ExplicitValue` | M | K\Lists | none | `ExplicitList` list, explicitValue | list with name and value columns | OQ-06 |
| `Pol_List_ValuePrefix` | U | K\Lists | none | `PrefixList` list, valuePrefix `Item` | list with a value prefix | OQ-06 |
| `Pol_List_Expandable` | M | K\Lists | none | `ExpandList` list, expandable | list expandable | OQ-06 |
| `Pol_List_PolicyValueName` | M | K\Lists | `ListSwitch` | `SwitchedList` list, key K\Lists\Switched | list under a policy that has a value name | OQ-04, OQ-06 |
| `Pol_Shared_Zeta` | M | K\Shared | `Zeta` | none | shared key, defined first, sorts last | OQ-01, OQ-04 |
| `Pol_Shared_Alpha` | M | K\Shared | `Alpha` | none | shared key, defined second, sorts first | OQ-01, OQ-04 |
| `Pol_Shared_Mid` | M | K\Shared | `Mid` | none | shared key, defined third | OQ-01, OQ-04 |
| `Pol_Shared_ElementsReverseOrder` | M | K\Shared | `Gate` | `ZetaElement` text, then `AlphaElement` decimal | one policy, several values, reverse name order | OQ-01 |
| `Pol_SharedList_One` | M | K\Shared | none | `SharedListOne` list, key K\Shared\SharedList | two policies sharing a list key | OQ-04, OQ-06 |
| `Pol_SharedList_Two` | M | K\Shared | none | `SharedListTwo` list, key K\Shared\SharedList | two policies sharing a list key | OQ-04, OQ-06 |
| `Pol_Sort_Zulu` | M | K\Sort | `SortZulu` | none | sort case, display name "Zulu sort case" | |
| `Pol_Sort_LowerAlpha` | M | K\Sort | `SortLowerAlpha` | none | sort case, "alpha policy" | |
| `Pol_Sort_Digit10` | M | K\Sort | `SortDigit10` | none | sort case, "Policy 10" | |
| `Pol_Sort_Digit2` | M | K\Sort | `SortDigit2` | none | sort case, "Policy 2" | |
| `Pol_Sort_LeadingSymbol` | M | K\Sort | `SortLeadingSymbol` | none | sort case, "#Symbol policy" | |
| `Pol_Sort_NonAscii` | M | K\Sort | `SortNonAscii` | none | sort case, "Éclair policy" (non-ASCII letter) | |
| `Pol_Sort_Digit1` | M | K\Sort | `SortDigit1` | none | sort case, "Policy 1" | |
| `Pol_Sort_UpperAlpha` | M | K\Sort | `SortUpperAlpha` | none | sort case, "Alpha Policy" (mixed case against "alpha policy") | |
| `Pol_Amp_DisplayName` | M | K\Amp | `AmpName` | none | display name with a literal `&` | |
| `Pol_Amp_LabelSingle` | M | K\Amp | none | `AmpText` text, `AmpCheck` boolean, `AmpNumber` decimal, `AmpChoice` enum | labels and drop-down items with a literal `&`, a boolean with no values | OQ-02 |
| `Pol_Amp_LabelDouble` | M | K\Amp | none | `DoubleAmpText` text | label with `&&` | |
| `Pol_Explain_MultiParagraph` | M | K\Explain | `MultiParagraph` | none | explain text with three paragraphs | |
| `Pol_Explain_EmptyString` | M | K\Explain | `EmptyString` | none | explainText refers to an empty string | |
| `Pol_Explain_AttributeOmitted` | M | K\Explain | `AttributeOmitted` | none | no explainText attribute | |
| `Pol_Nested_MachineTop` | M | K\Nested | `MachineTop` | none | nested categories, level one of three | |
| `Pol_Nested_MachineMid` | M | K\Nested | `MachineMid` | none | nested categories, level two of three | |
| `Pol_Nested_MachineDeep` | M | K\Nested | `MachineDeep` | none | nested categories, level three of three | |
| `Pol_Nested_UserDeep` | U | K\Nested | `UserDeep` | none | nested user branch, category above has no policy | |

Other coverage notes:

- `supportedOn`: the policies use `bwcbase:SUPPORTED_BwcAny`, `bwcbase:SUPPORTED_BwcNewer`, and `bwcbase:SUPPORTED_BwcOlder`, all defined in the base file and used through the prefix. `Pol_Both_WithElements` uses `bwcoracle:SUPPORTED_LocalOnly`, defined in the same file.
- OQ-05 (an emptied file), OQ-07 (`gpt.ini` pairs and comment-only bumps), and OQ-08 (`**` instructions) do not depend on a particular policy. The capture sequences use any two fixture policies, for example `Pol_Class_Machine` and `Pol_Class_User`, for those cases.
- OQ-09 (the Extra Registry Settings node) uses `Pol_Outside_KeyOutsideRoots` because its key is the one outside the policy roots, plus crafted `Registry.pol` files made by the capture tool.
- The ampersand strings are escaped in the ADML as `&amp;`. After XML parsing the text holds a single `&`.

## Bad fixtures

Each case has its own folder under `bad/`, its own namespace `BWC.Policies.Bad.<Case>`, one category, and one policy that would write under `K\Bad\<Case>`. The policy of every case is otherwise valid, so the single fault is the only reason for the error.

| Folder | Fault | Expected BWC result |
|---|---|---|
| `bad/MalformedXml` | The ADMX has a `policy` start tag with no end tag. | A load failure that names the file, line, and position. The other templates still load. |
| `bad/MissingAdml` | The ADMX exists and there is no `en-US` folder and no ADML. | A load failure that names the missing ADML path. |
| `bad/UndefinedString` | The policy display name refers to `$(string.Pol_Bad_NotDefined)`, which the ADML does not define. | A load failure that names the string id and the file. |
| `bad/UndefinedUsing` | The ADMX declares `using` for the namespace `BWC.Policies.Bad.UndefinedUsing.Ghost`, which no file defines. | A load failure that names the unresolved namespace. |
| `bad/DuplicatePolicy` | Two policies in one file have the name `Pol_Bad`. | A load failure that names the duplicate policy. |
| `bad/DtdEntity` | The ADMX has a `DOCTYPE` with an internal entity `bwcvalue` that is used as a value name. | BWC refuses the file because DTD processing is prohibited, and reports it as a load failure. For classic the oracle records what happens. |
| `bad/ListItemNoKey` | An `item` of an `enabledList` has no `key` attribute and the list has no `defaultKey`. | A load failure `MissingAttribute` that names `key`, the file, the line and the column. Classic refuses the file with "Expected attribute 'key' not found." (live capture). No content of the file is shown. |
| `bad/BooleanNoValueName` | A `boolean` element has no `valueName` attribute and only a `trueList` and a `falseList`. | A load failure `MissingAttribute` that names `valueName`, the file, the line and the column. Classic refuses the file with "Expected attribute 'valueName' not found." (live capture). No content of the file is shown. |

The entity is internal and harmless. No external entity or network reference exists in any file.

## Cleanup

The capture tool must delete these keys when it finishes and must assert that they are gone:

- `HKLM\Software\Policies\BWC-Oracle`
- `HKCU\Software\Policies\BWC-Oracle`
- `HKLM\Software\BWC-Oracle-Gpedit`
- `HKCU\Software\BWC-Oracle-Gpedit`

The key `Software\BWC-Oracle-Gpedit` is fixed by name and is the only key that lies outside the policy roots. The keys are created only if the policy refresh applies a captured `Registry.pol` to the registry.
