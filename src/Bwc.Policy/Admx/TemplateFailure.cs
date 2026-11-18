namespace Bwc.Policy.Admx;

/// <summary>The reason a template, a part of a template, or a folder could not be loaded.</summary>
public enum TemplateFailureCode
{
    /// <summary>A source folder does not exist.</summary>
    FolderNotFound,

    /// <summary>A template file, a source folder or a language folder is a reparse point.</summary>
    ReparsePoint,

    /// <summary>A template file is larger than <see cref="AdmxLimits.MaxFileBytes"/>.</summary>
    FileTooLarge,

    /// <summary>A template file name is longer than <see cref="AdmxLimits.MaxFileNameLength"/>.</summary>
    FileNameTooLong,

    /// <summary>The file system refused to read a file. <see cref="TemplateFailure.Detail"/> holds the HRESULT.</summary>
    Unreadable,

    /// <summary>The file is not well formed XML.</summary>
    MalformedXml,

    /// <summary>The file has a DOCTYPE, which is never processed.</summary>
    DtdProhibited,

    /// <summary>The file nests elements deeper than <see cref="AdmxLimits.MaxXmlDepth"/>.</summary>
    XmlTooDeep,

    /// <summary>The file is well formed but is not an ADMX or ADML template.</summary>
    NotATemplate,

    /// <summary>No ADML was found for an ADMX in the requested language or in en-US.</summary>
    MissingAdml,

    /// <summary>A second file in the same source defines an already loaded namespace.</summary>
    DuplicateNamespace,

    /// <summary>A file in a later source defines a namespace that an earlier source already defines.</summary>
    NamespaceShadowed,

    /// <summary>A name, key or value name is longer than its limit.</summary>
    NameTooLong,

    /// <summary>An ADML string is longer than <see cref="AdmxLimits.MaxStringLength"/>.</summary>
    StringTooLong,

    /// <summary>A required attribute or element is missing.</summary>
    MissingAttribute,

    /// <summary>An attribute holds a value the parser cannot use.</summary>
    InvalidAttributeValue,

    /// <summary>A policy name is defined twice in one file.</summary>
    DuplicatePolicy,

    /// <summary>A category name is defined twice in one file.</summary>
    DuplicateCategory,

    /// <summary>An element id is used twice in one policy.</summary>
    DuplicateElement,

    /// <summary>A supported-on definition name is defined twice in one file.</summary>
    DuplicateSupportedOn,

    /// <summary>A string id is defined twice in one ADML.</summary>
    DuplicateString,

    /// <summary>A presentation id is defined twice in one ADML.</summary>
    DuplicatePresentation,

    /// <summary>A string reference names no string of the ADML.</summary>
    UndefinedString,

    /// <summary>A presentation reference names no presentation of the ADML.</summary>
    UndefinedPresentation,

    /// <summary>A presentation control names an element id that the policy does not have.</summary>
    UndefinedPresentationRef,

    /// <summary>A using declaration names a namespace that no loaded file defines.</summary>
    UndefinedUsing,

    /// <summary>A reference uses a prefix that the file does not declare.</summary>
    UndefinedPrefix,

    /// <summary>A category reference names no category.</summary>
    UndefinedCategory,

    /// <summary>A policy has no parent category.</summary>
    MissingCategory,

    /// <summary>A supported-on reference names no definition.</summary>
    UndefinedSupportedOn,

    /// <summary>Category parents form a loop.</summary>
    CategoryCycle,

    /// <summary>An unexpected error while loading one file. The file is dropped and the type name is in the detail.</summary>
    InternalError,
}

/// <summary>
/// One problem found while loading templates. The engine never formats user text, so the UI picks the wording from
/// <see cref="Code"/> and uses <see cref="Detail"/> as the faulty identifier.
/// </summary>
/// <param name="Code">The reason.</param>
/// <param name="FilePath">The file or folder the problem belongs to.</param>
/// <param name="Line">The one-based line, or 0 when unknown.</param>
/// <param name="Position">The one-based position in the line, or 0 when unknown.</param>
/// <param name="Detail">The faulty identifier (a string id, a namespace, a name), or an empty string.</param>
public sealed record TemplateFailure(TemplateFailureCode Code, string FilePath, int Line, int Position, string Detail);
