﻿using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using ClangSharp;
using ClangSharp.Interop;
using Type = ClangSharp.Type;

namespace Bindgen.NET;

/// <summary>
/// Static class for generating bindings from configuration classes.
/// </summary>
public static class BindingGenerator
{
    private const string MacroPrefix = "BindgenMacro";
    private const string AnonymousPrefix = "Anonymous";

    private static readonly string BuiltInHeaderDirectory = Path.GetFullPath("BindgenBuiltInHeaders");

    private static BindingOptions _options = new ();

    /// <summary>
    /// Generates bindings for all configurations given.
    /// </summary>
    /// <param name="options">An array of configuration options to use when generating bindings.</param>
    /// <returns>An array containing strings of generated source code for all binding configurations.</returns>
    public static KeyValuePair<string, string>[] Generate(ReadOnlySpan<BindingOptions> options)
    {
        BindingOptions[] optionsArray = options.ToArray();

        return optionsArray
            .Select(option => new KeyValuePair<string, string>(option.OutputFile, Generate(option)))
            .ToArray();
    }

    /// <summary>
    /// Generates bindings based on the values specified in the <c>options</c> parameter.
    /// </summary>
    /// <param name="options">The configuration options to use when generating bindings.</param>
    /// <returns>A string of the generated source code.</returns>
    public static string Generate(BindingOptions options)
    {
        _options = options;

        Diagnostic.CurrentDiagnosticLevel = options.DiagnosticLevel;

        string inputFileName = GetInputFileName();

        List<string> arguments = options.IncludeDirectories
            .Union(options.SystemIncludeDirectories)
            .Select(includeDirectory => "-I" + Path.GetFullPath(includeDirectory))
            .ToList();

        List<CXUnsavedFile> unsavedFiles = new();
        CXTranslationUnit_Flags flags = default;

        if (options.GenerateMacros)
            flags |= CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord;

        if (options.TreatInputFileAsRawSourceCode)
            unsavedFiles.Add(CXUnsavedFile.Create(inputFileName, options.InputFile));
        else if (!Path.Exists(inputFileName))
            throw new ArgumentException($"Input file at path \"{inputFileName}\" does not exist.", nameof(options));

        if (options.IncludeBuiltInClangHeaders)
        {
            arguments.Add($"-I{BuiltInHeaderDirectory}");

            foreach ((string fileName, string fileContents) in ClangHeaders.Headers)
                unsavedFiles.Add(CXUnsavedFile.Create(Path.Combine(BuiltInHeaderDirectory, fileName), fileContents));
        }

        CXIndex index = CXIndex.Create();
        CXErrorCode errorCode = CXTranslationUnit.TryParse(index, inputFileName, arguments.ToArray(), unsavedFiles.ToArray(), flags, out CXTranslationUnit handle);

        foreach (CXUnsavedFile unsavedFile in unsavedFiles)
            unsavedFile.Dispose();

        ProcessDiagnostics(errorCode, handle);

        TranslationUnit translationUnit = TranslationUnit.GetOrCreate(handle);

        if (options.GenerateMacros)
            translationUnit = ProcessMacros(index, translationUnit, arguments.ToArray(), flags);

        string output = GenerateTranslationUnitDecl(translationUnit.TranslationUnitDecl);
        string formattedOutput = CodeFormatter.Format(output);

        if (options.GenerateToFilesystem)
        {
            File.WriteAllText(options.OutputFile, formattedOutput);
            Diagnostic.Log(DiagnosticLevel.Info, $"Generated {Path.GetFullPath(options.OutputFile)} from {inputFileName}");
        }

        translationUnit.Dispose();
        index.Dispose();

        return formattedOutput;
    }

    private static string GetInputFileName()
    {
        return _options.TreatInputFileAsRawSourceCode ? _options.RawSourceName : Path.GetFullPath(_options.InputFile);
    }

    // TODO: Handle errors
    private static void ProcessDiagnostics(CXErrorCode errorCode, CXTranslationUnit handle)
    {
        if (handle.NumDiagnostics != 0)
        {
            for (uint i = 0; i < handle.NumDiagnostics; i++)
            {
                using CXDiagnostic diagnostic = handle.GetDiagnostic(i);
                Diagnostic.Log(DiagnosticLevel.Warning, diagnostic.Format(CXDiagnostic.DefaultDisplayOptions).ToString());
            }
        }
    }

    // We collect all macro definition records and append them to the end of the file as type-inferred auto variables.
    private static TranslationUnit ProcessMacros(CXIndex index, TranslationUnit translationUnit, ReadOnlySpan<string> arguments, CXTranslationUnit_Flags flags)
    {
        string inputFileName = GetInputFileName();
        CXTranslationUnit translationUnitHandle = translationUnit.Handle;

        CXFile file = translationUnitHandle.GetFile(inputFileName);
        ReadOnlySpan<byte> inputFileContents = translationUnitHandle.GetFileContents(file, out UIntPtr _);

        StringBuilder newFileBuilder = new();
        newFileBuilder.AppendLine(Encoding.UTF8.GetString(inputFileContents));

        MacroDefinitionRecord[] macroDefinitionRecords = translationUnit.TranslationUnitDecl.CursorChildren
            .OfType<MacroDefinitionRecord>()
            .Where(macro => !macro.Location.IsInSystemHeader)
            .Where(macro => !IsFromNamelessFile(macro))
            .Where(IsUserInclude)
            .ToArray();

        foreach (MacroDefinitionRecord macroDefinitionRecord in macroDefinitionRecords)
            newFileBuilder.AppendLine(GenerateMacroDummy(macroDefinitionRecord));

        translationUnit.Dispose();

        List<CXUnsavedFile> unsavedFiles = new();
        unsavedFiles.Add(CXUnsavedFile.Create(inputFileName, newFileBuilder.ToString()));

        if (_options.IncludeBuiltInClangHeaders)
        {
            foreach ((string fileName, string fileContents) in ClangHeaders.Headers)
                unsavedFiles.Add(CXUnsavedFile.Create(Path.Combine(BuiltInHeaderDirectory, fileName), fileContents));
        }

        CXTranslationUnit handle = CXTranslationUnit.Parse(index, inputFileName, arguments.ToArray(), unsavedFiles.ToArray(), flags & ~CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord);

        foreach (CXUnsavedFile unsavedFile in unsavedFiles)
            unsavedFile.Dispose();

        return TranslationUnit.GetOrCreate(handle);
    }

    private static string GetSourceRangeContents(CXTranslationUnit translationUnit, CXSourceRange sourceRange)
    {
        sourceRange.Start.GetFileLocation(out CXFile startFile, out uint _, out uint _, out uint startOffset);
        sourceRange.End.GetFileLocation(out CXFile endFile, out uint _, out uint _, out uint endOffset);

        if (startFile != endFile)
            return string.Empty;

        ReadOnlySpan<byte> fileContents = translationUnit.GetFileContents(startFile, out UIntPtr _);
        fileContents = fileContents.Slice(unchecked((int)startOffset), unchecked((int)(endOffset - startOffset)));

        return Encoding.UTF8.GetString(fileContents);
    }

    // We don't want to generate bindings for stuff inside of system includes. We use this to filter for non-system headers.
    private static bool IsUserInclude(Cursor cursor)
    {
        cursor.Location.GetFileLocation(out CXFile file, out _, out _, out _);
        string fileName = file.Name.ToString();

        if (fileName.StartsWith(BuiltInHeaderDirectory, StringComparison.Ordinal))
            return false;

        return _options.SystemIncludeDirectories
            .Select(Path.GetFullPath)
            .All(fullIncludeDirectory => !fileName.StartsWith(fullIncludeDirectory, StringComparison.Ordinal));
    }

    // TODO: Are there cases where nameless files are fine?
    // Don't generate macros from files with empty names because it includes some junk.
    private static bool IsFromNamelessFile(Cursor cursor)
    {
        cursor.Location.GetFileLocation(out CXFile file, out uint _, out uint _, out uint _);
        return string.IsNullOrEmpty(file.Name.ToString());
    }

    private static bool RecordHasDefinition(RecordDecl recordDecl)
    {
        while (!recordDecl.IsThisDeclarationADefinition)
        {
            if (recordDecl.Definition == null)
                return false;

            recordDecl = recordDecl.Definition;
        }

        return true;
    }

    private static RecordDecl GetRecordDefinition(RecordDecl recordDecl)
    {
        while (!recordDecl.IsThisDeclarationADefinition)
        {
            if (recordDecl.Definition == null)
                break;

            recordDecl = recordDecl.Definition;
        }

        return recordDecl;
    }

    private static bool IsType<T>(Type type, [MaybeNullWhen(false)] out T value) where T : Type?
    {
        if (type is T t)
        {
            value = t;
            return true;
        }

        if (type is ElaboratedType elaboratedType)
            return IsType(elaboratedType.CanonicalType, out value);

        if (type is PointerType pointerType)
            return IsType(pointerType.PointeeType, out value);

        value = default;
        return false;
    }

    private static int GetConstantArraySize(ConstantArrayType constantArrayType)
    {
        long size = constantArrayType.Size;

        while (constantArrayType.ElementType is ConstantArrayType elementType)
        {
            size *= elementType.Size;
            constantArrayType = elementType;
        }

        return (int)size;
    }

    private static bool IsSupportedFixedSizedBufferType(string typeName)
    {
        switch (typeName)
        {
            case "bool":
            case "byte":
            case "char":
            case "double":
            case "float":
            case "int":
            case "long":
            case "sbyte":
            case "short":
            case "ushort":
            case "uint":
            case "ulong":
                return true;
            default:
                return false;
        }
    }

    private static string GenerateFixedBufferName(string name)
    {
        return name + "_FixedBuffer";
    }

    private static string GenerateExternFieldName(string name)
    {
        return name + "_Ptr";
    }

    private static string GenerateBindgenInternal()
    {
        string[] quotedFilePaths = _options.DllFilePaths.Select(path => "\"" + path + "\"").ToArray();

        return $$"""
            public partial class BindgenInternal
            {
                public const string DllImportPath = "{{_options.DllImportPath}}";

                static BindgenInternal()
                {
                    DllFilePaths = new System.Collections.Generic.List<string> { {{string.Join(',', quotedFilePaths)}} };
                }
            }

            {{Sources.Internal}}
        """;
    }

    private static string GenerateTranslationUnitDecl(TranslationUnitDecl translationUnitDecl)
    {
        Cursor[] cursors = translationUnitDecl.CursorChildren
            .Where(cursor => cursor is FunctionDecl or RecordDecl or EnumDecl or VarDecl)
            .Where(cursor => !cursor.Location.IsInSystemHeader)
            .Where(IsUserInclude)
            .ToArray();

        FunctionDecl[] functionDecls = cursors
            .OfType<FunctionDecl>()
            .OrderBy(x => x.Name)
            .ToArray();

        RecordDecl[] recordDecls = cursors
            .OfType<RecordDecl>()
            .OrderBy(x => x.Name)
            .GroupBy(x => x.Name)
            .Select(x => x.First())
            .ToArray();

        EnumDecl[] enumDecls = cursors
            .OfType<EnumDecl>()
            .OrderBy(x => x.Name)
            .ToArray();

        VarDecl[] varDecls = cursors
            .OfType<VarDecl>()
            .OrderBy(x => x.Name)
            .ToArray();

        VarDecl[] macroVarDecls = varDecls
            .Where(x => x.Name.StartsWith(MacroPrefix, StringComparison.Ordinal))
            .ToArray();

        VarDecl[] externVarDecls = varDecls
            .Where(x => x.HasExternalStorage)
            .ToArray();

        StringBuilder output = new();

        foreach (FunctionDecl functionDecl in functionDecls)
            output.AppendLine(GenerateFunctionDecl(functionDecl));

        foreach (RecordDecl recordDecl in recordDecls)
            output.AppendLine(GenerateRecordDecl(recordDecl));

        foreach (EnumDecl enumDecl in enumDecls)
            output.AppendLine(GenerateEnumDecl(enumDecl));

        foreach (EnumDecl enumDecl in enumDecls)
            output.AppendLine(GenerateEnumDeclConstants(enumDecl));

        if (_options.GenerateMacros)
        {
            foreach (VarDecl varDecl in macroVarDecls)
                output.AppendLine(GenerateMacroVarDecl(varDecl));
        }

        if (_options.GenerateExternVariables)
        {
            foreach (VarDecl varDecl in externVarDecls)
                output.AppendLine(GenerateExternVarDeclField(varDecl));

            foreach (VarDecl varDecl in externVarDecls)
                output.AppendLine(GenerateExternVarDeclProperty(varDecl));
        }

        return $$"""
            #nullable enable
            {{(_options.SuppressedWarnings.Count > 0 ? $"#pragma warning disable {string.Join(' ', _options.SuppressedWarnings)}" : string.Empty)}}
            namespace {{_options.Namespace}}
            {
                public static unsafe partial class {{_options.Class}}
                {
                    {{output}}
                    {{GenerateBindgenInternal()}}
                }
            }
            {{(_options.SuppressedWarnings.Count > 0 ? $"#pragma warning restore {string.Join(' ', _options.SuppressedWarnings)}" : string.Empty)}}
            #nullable disable
        """;
    }

    private static string GenerateFunctionDecl(FunctionDecl functionDecl)
    {
        IEnumerable<string> parameters = functionDecl.Parameters
            .Select(parameter => $"{GetTypeName(parameter.Type)} {GetValidIdentifier(parameter.Name)}")
            .ToArray();

        return $@"
            {(_options.GenerateSuppressGcTransition ? "[System.Runtime.InteropServices.SuppressGCTransition]" : string.Empty)}
            [System.Runtime.InteropServices.DllImport(BindgenInternal.DllImportPath, EntryPoint = ""{GetValidIdentifier(functionDecl.NameInfoName, false)}"", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
            public static extern {GetTypeName(functionDecl.ReturnType)} {GetValidIdentifier(functionDecl.Name)}({string.Join(", ", parameters)});
        ";
    }

    private static string GenerateRecordDecl(RecordDecl recordDecl)
    {
        if (RecordHasDefinition(recordDecl) && !recordDecl.IsThisDeclarationADefinition)
            return GenerateRecordDecl(GetRecordDefinition(recordDecl));

        string recordName = GetRemappedCursorName(recordDecl);

        FieldDecl[] fieldsDecls = recordDecl.Decls
            .OfType<FieldDecl>()
            .ToArray();

        IndirectFieldDecl[] indirectFieldDecls = recordDecl.Decls
            .OfType<IndirectFieldDecl>()
            .ToArray();

        FieldDecl[] fixedBufferFieldDecls = recordDecl.Decls
            .OfType<FieldDecl>()
            .Where(fieldDecl => fieldDecl.Type is ConstantArrayType)
            .Where(fieldDecl => !IsSupportedFixedSizedBufferType(GetTypeName(((ConstantArrayType)fieldDecl.Type).ElementType)))
            .ToArray();

        RecordDecl[] recordFieldsDecls = recordDecl.Decls
            .OfType<RecordDecl>()
            .ToArray();

        StringBuilder fields = new();

        foreach (FieldDecl fieldDecl in fieldsDecls)
        {
            if (recordDecl.IsUnion)
                fields.AppendLine("[System.Runtime.InteropServices.FieldOffset(0)]");

            string fieldName = GetValidIdentifier(fieldDecl.Name);
            string typeName = GetRemappedTypeName(fieldDecl.Type);

            if (fieldDecl.IsAnonymousField)
                fieldName = GetRemappedTypeName(fieldDecl.Type) + "_Field";

            if (fieldDecl.Type is ConstantArrayType constantArrayType && IsSupportedFixedSizedBufferType(GetTypeName(constantArrayType.ElementType)))
            {
                int size = GetConstantArraySize(constantArrayType);
                fields.AppendLine(CultureInfo.InvariantCulture, $"public fixed {GetTypeName(constantArrayType.ElementType)} {GetValidIdentifier(fieldDecl.Name)}[{size}];");
                continue;
            }

            if (fieldDecl.Type is ConstantArrayType)
                typeName = GenerateFixedBufferName(fieldName);

            bool commentFunctionPointer = IsType<FunctionProtoType>(fieldDecl.Type, out FunctionProtoType? functionProtoType) && !_options.GenerateFunctionPointers;

            fields.AppendLine(CultureInfo.InvariantCulture, $"public {typeName} {fieldName}; {(commentFunctionPointer ? "// " + GetCSharpFunctionPointer(functionProtoType!) : string.Empty)}");
        }

        foreach (IndirectFieldDecl indirectFieldDecl in indirectFieldDecls)
        {
            IDeclContext declContext = indirectFieldDecl.AnonField.DeclContext!;

            if (declContext is RecordDecl contextRecordDecl)
            {
                string typeName = GetRemappedTypeName(indirectFieldDecl.Type);
                string declContextName = GetRemappedCursorName(contextRecordDecl);
                string fieldName = GetValidIdentifier(indirectFieldDecl.Name);

                if (IsAnonymous(indirectFieldDecl.Type))
                    typeName = $"{declContextName}.{typeName}";

                fields.AppendLine(CultureInfo.InvariantCulture,
                    $"public ref {typeName} {fieldName} => ref {declContextName}_Field.{fieldName};");
            }
        }

        foreach (FieldDecl fieldDecl in fixedBufferFieldDecls)
        {
            ConstantArrayType constantArrayType = (ConstantArrayType)fieldDecl.Type;

            string fieldName = GenerateFixedBufferName(GetValidIdentifier(fieldDecl.Name));
            string typeName = GetTypeName(constantArrayType.ElementType);
            int arraySize = GetConstantArraySize(constantArrayType);

            StringBuilder fixedBufferFields = new();

            for (int i = 0; i < arraySize; i++)
                fixedBufferFields.AppendLine(CultureInfo.InvariantCulture, $"public {typeName} Item{i};");

            string indexer;

            if (IsType(constantArrayType.ElementType, out PointerType _))
                indexer = $$"""
                    public ref {{typeName}} this[int index]
                    {
                        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                        get
                        {
                            if (index >= {{arraySize}})
                                throw new System.ArgumentOutOfRangeException($"Index {index} is out of range.");

                            fixed ({{typeName}}* pThis = &Item0)
                                return ref pThis[index];
                        }
                    }
                """;
            else
                indexer = $$"""
                    public ref {{typeName}} this[int index] => ref AsSpan()[index];

                    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                    public System.Span<{{typeName}}> AsSpan() => System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref Item0, {{arraySize}});
                """;

            string fixedBufferSource = $$"""
                public partial struct {{fieldName}}
                {
                    {{fixedBufferFields}}
                    {{indexer}}
                }
            """;

            fields.AppendLine(fixedBufferSource);
        }

        foreach (RecordDecl recordFieldDecl in recordFieldsDecls)
            fields.AppendLine(GenerateRecordDecl(recordFieldDecl));

        return $@"
            {(recordDecl.IsUnion ? "[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]" : "")}
            public partial struct {recordName}
            {{
                {fields}
            }} 
        ";
    }

    private static string GenerateEnumDecl(EnumDecl enumDecl)
    {
        List<string> enumMembers = new ();
        bool hasNegatives = false;

        foreach (EnumConstantDecl enumConstant in enumDecl.Enumerators)
        {
            if (enumConstant.IsNegative)
                hasNegatives = true;

            enumMembers.Add(GenerateEnumConstantDecl(enumConstant));
        }

        return $@"
            public enum {GetCursorName(enumDecl)} : {GetIntegerName(enumDecl.IntegerType.Handle.SizeOf, hasNegatives, "INVALID_ENUM_INTEGER")}
            {{ 
                {string.Join(",\n", enumMembers)}
            }}
        ";
    }

    private static string GenerateEnumConstantDecl(EnumConstantDecl enumConstantDecl)
    {
        string value = enumConstantDecl.IsSigned
            ? enumConstantDecl.InitVal.ToString(CultureInfo.InvariantCulture)
            : enumConstantDecl.UnsignedInitVal.ToString(CultureInfo.InvariantCulture);

        return $"{GetValidIdentifier(enumConstantDecl.Name)} = {value}";
    }

    private static string GenerateEnumDeclConstants(EnumDecl enumDecl)
    {
        string enumName = GetCursorName(enumDecl);

        IEnumerable<string> constantFields = enumDecl.Decls.OfType<EnumConstantDecl>().Select(enumConstantDecl =>
        {
            string enumMemberName = GetValidIdentifier(enumConstantDecl.Name);
            return $"public const {enumName} {enumMemberName} = {enumName}.{enumMemberName};";
        });

        return string.Join("\n", constantFields);
    }

    // TODO: Add binding option to allow for generating refs to extern variables
    private static string GenerateMacroVarDecl(VarDecl varDecl)
    {
        if (!varDecl.Name.StartsWith(MacroPrefix, StringComparison.Ordinal))
            return string.Empty;

        if (!varDecl.HasInit)
            return string.Empty;

        Expr init = varDecl.Init;
        CXEvalResult result = varDecl.Handle.Evaluate;

        string typeName = GetTypeName(varDecl.Type);
        string expression;

        switch (result.Kind)
        {
            case CXEvalResultKind.CXEval_Float:
                expression = init.Type.Kind switch
                {
                    CXTypeKind.CXType_Double =>  result.AsDouble.ToString(CultureInfo.InvariantCulture),
                    CXTypeKind.CXType_Float =>  ((float)result.AsDouble).ToString(CultureInfo.InvariantCulture),
                    CXTypeKind.CXType_LongDouble =>  ((decimal)result.AsDouble).ToString(CultureInfo.InvariantCulture),
                    _ => $"INVALID_FLOAT_{init.Type.Kind}"
                };
                break;
            case CXEvalResultKind.CXEval_Int:
                expression = init.Type.Handle.SizeOf switch
                {
                    1 => result.IsUnsignedInt ? ((byte)result.AsUnsigned).ToString(CultureInfo.InvariantCulture) : ((sbyte)result.AsLongLong).ToString(CultureInfo.InvariantCulture),
                    2 => result.IsUnsignedInt ? ((ushort)result.AsUnsigned).ToString(CultureInfo.InvariantCulture) : ((short)result.AsLongLong).ToString(CultureInfo.InvariantCulture),
                    4 => result.IsUnsignedInt ? ((uint)result.AsUnsigned).ToString(CultureInfo.InvariantCulture) : ((int)result.AsLongLong).ToString(CultureInfo.InvariantCulture),
                    8 => result.IsUnsignedInt ? result.AsUnsigned.ToString(CultureInfo.InvariantCulture) : result.AsLongLong.ToString(CultureInfo.InvariantCulture),
                    _ => $"INVALID_INTEGER_SIZEOF_{init.Type.Handle.SizeOf}"
                };
                break;
            case CXEvalResultKind.CXEval_StrLiteral:
                typeName = "string";
                expression = "\"" + result.AsStr + "\"";
                break;
            case CXEvalResultKind.CXEval_ObjCStrLiteral:
            case CXEvalResultKind.CXEval_CFStr:
            case CXEvalResultKind.CXEval_Other:
            case CXEvalResultKind.CXEval_UnExposed:
            default:
                return "";
        }

        return string.IsNullOrEmpty(expression)
            ? string.Empty
            : $"public const {typeName} {GetValidIdentifier(varDecl.Name[MacroPrefix.Length..])} = {expression};";
    }

    private static string GenerateExternVarDeclField(VarDecl varDecl)
    {
        string validName = GetValidIdentifier(varDecl.Name);
        string fieldName = GenerateExternFieldName(validName);
        return varDecl.HasExternalStorage ? $"private static void* {fieldName};" : "";
    }

    private static string GenerateExternVarDeclProperty(VarDecl varDecl)
    {
        if (!varDecl.HasExternalStorage)
            return "";

        string typeName = GetTypeName(varDecl.Type);
        string validName = GetValidIdentifier(varDecl.Name);
        string fieldName = GenerateExternFieldName(validName);

        // We can't use Unsafe.AsRef<T>(void*) because T can't be a pointer.
        return $"public static ref {typeName} {validName} => ref *({typeName}*)({fieldName} == null ? BindgenInternal.LoadDllSymbol(\"{varDecl.Name}\", out {fieldName}) : {fieldName});";
    }

    // This converts value-like macros to type-inferred variables so we can get access to it's type information.
    // The macro's constants will be generated in GenerateMacroVarDecl().
    private static string GenerateMacroDummy(MacroDefinitionRecord macro)
    {
        if (macro.IsFunctionLike)
            return string.Empty;

        CXTranslationUnit translationUnitHandle = macro.TranslationUnit.Handle;
        Span<CXToken> tokens = translationUnitHandle.Tokenize(macro.Extent);

        bool hasNoValue = tokens[0].Kind != CXTokenKind.CXToken_Identifier ||
                          tokens[0].GetSpelling(translationUnitHandle).CString != macro.Spelling ||
                          tokens.Length == 1;

        if (hasNoValue)
            return string.Empty;

        CXSourceLocation sourceRangeEnd = tokens[^1].GetExtent(translationUnitHandle).End;
        CXSourceLocation sourceRangeStart = tokens[1].GetLocation(translationUnitHandle);
        CXSourceRange sourceRange = CXSourceRange.Create(sourceRangeStart, sourceRangeEnd);

        string value = GetSourceRangeContents(translationUnitHandle, sourceRange);

        return $"const __auto_type {MacroPrefix}{macro.Name} = {value};";
    }

    private static string GetCSharpFunctionPointer(FunctionProtoType functionProtoType)
    {
        List<string> parameters = functionProtoType.ParamTypes.Select(GetTypeName).ToList();
        parameters.Add(GetTypeName(functionProtoType.ReturnType));
        return $"delegate* unmanaged<{string.Join(", ", parameters)}>";
    }

    private static bool IsAnonymous(Type type)
    {
        string? cursorName = null;

        if (IsType(type, out RecordType? recordType))
            cursorName = GetCursorName(recordType!.Decl);

        if (IsType(type, out EnumType? enumType))
            cursorName = GetCursorName(enumType!.Decl);

        return cursorName?.StartsWith(AnonymousPrefix, StringComparison.InvariantCulture) ?? false;
    }

    private static string GetAnonymousName(Cursor cursor, string kind)
    {
        cursor.Location.GetFileLocation(out CXFile file, out uint line, out uint column, out _);
        string fileName = Path.GetFileNameWithoutExtension(file.Name.ToString());
        return $"{AnonymousPrefix}{kind}_{fileName}_L{line}_C{column}";
    }

    private static string GetCursorName(NamedDecl namedDecl)
    {
        string name = GetValidIdentifier(namedDecl.Name);

        if (namedDecl is TypeDecl typeDecl)
        {
            bool isAnonymous =
                string.IsNullOrWhiteSpace(name) ||
                name.StartsWith("struct (unnamed", StringComparison.Ordinal) ||
                name.StartsWith("union (unnamed", StringComparison.Ordinal) ||
                name.StartsWith("enum (unnamed", StringComparison.Ordinal) ||
                name.StartsWith("struct (anonymous", StringComparison.Ordinal) ||
                name.StartsWith("union (anonymous", StringComparison.Ordinal) ||
                name.StartsWith("enum (anonymous", StringComparison.Ordinal) ||
                name.StartsWith("(unnamed struct", StringComparison.Ordinal) ||
                name.StartsWith("(unnamed union", StringComparison.Ordinal) ||
                name.StartsWith("(unnamed enum", StringComparison.Ordinal);

            return isAnonymous ? GetAnonymousName(typeDecl, typeDecl.TypeForDecl.KindSpelling) : name;
        }

        return name;
    }

    private static string GetSignedIntegerName(long size, string? error = null)
    {
        return size switch
        {
            1 => "sbyte",
            2 => "short",
            4 => "int",
            8 => "long",
            _ => error ?? "INVALID_SIGNED_INTEGER"
        };
    }

    private static string GetUnsignedIntegerName(long size, string? error = null)
    {
        return size switch
        {
            1 => "byte",
            2 => "ushort",
            4 => "uint",
            8 => "ulong",
            _ => error ?? "INVALID_UNSIGNED_INTEGER"
        };
    }

    private static string GetIntegerName(long size, bool signed, string? error = null)
    {
        return signed ? GetSignedIntegerName(size, error) : GetUnsignedIntegerName(size, error);
    }

    private static string GetRemappedCursorName(NamedDecl namedDecl)
    {
        string name = GetCursorName(namedDecl);

        if (namedDecl is RecordDecl recordDecl && name.StartsWith(AnonymousPrefix, StringComparison.Ordinal) && recordDecl.Parent is RecordDecl parentRecordDecl)
        {
            FieldDecl? matchingField = parentRecordDecl.Fields
                .FirstOrDefault(fieldDecl => fieldDecl.Type.CanonicalType == recordDecl.TypeForDecl.CanonicalType);

            if (matchingField is not null && !string.IsNullOrEmpty(matchingField.Name))
                return $"{GetValidIdentifier(matchingField.Name)}_AnonymousRecord";
        }

        return name;
    }

    private static string GetRemappedTypeName(Type type)
    {
        string name = GetTypeName(type);

        if (IsType<RecordType>(type, out RecordType? recordType) && name.StartsWith(AnonymousPrefix, StringComparison.Ordinal) && recordType.Decl.Parent is RecordDecl parentRecordDecl)
        {
            RecordDecl recordDecl = recordType.Decl;

            FieldDecl? matchingField = parentRecordDecl.Fields
                .FirstOrDefault(fieldDecl => fieldDecl.Type.CanonicalType == recordDecl.TypeForDecl.CanonicalType);

            if (matchingField is not null && !string.IsNullOrEmpty(matchingField.Name))
                return $"{GetValidIdentifier(matchingField.Name)}_AnonymousRecord";
        }

        return name;
    }

    private static string GetTypeName(Type type)
    {
        if (type is AutoType autoType)
            return GetTypeName(autoType.GetDeducedType);

        if (type is BuiltinType builtinType)
        {
            switch (builtinType.Kind)
            {
                case CXTypeKind.CXType_Bool:
                    return "byte";
                case CXTypeKind.CXType_Float:
                    return "float";
                case CXTypeKind.CXType_Double:
                    return "double";
                case CXTypeKind.CXType_LongDouble:
                    return "decimal";
                case CXTypeKind.CXType_Void:
                    return "void";
                case CXTypeKind.CXType_Char16:
                case CXTypeKind.CXType_Char32:
                case CXTypeKind.CXType_Char_S:
                case CXTypeKind.CXType_Char_U:
                case CXTypeKind.CXType_SChar:
                case CXTypeKind.CXType_UChar:
                case CXTypeKind.CXType_WChar:
                    return GetIntegerName(
                        builtinType.Handle.SizeOf,
                        builtinType.Handle.IsSigned,
                        $"INVALID_CHAR_{builtinType.Kind}");
                case CXTypeKind.CXType_Short:
                case CXTypeKind.CXType_Int:
                case CXTypeKind.CXType_Long:
                case CXTypeKind.CXType_LongLong:
                    return GetSignedIntegerName(builtinType.Handle.SizeOf, $"INVALID_SIGNED_INTEGER_{builtinType.Kind}_SIZEOF_{builtinType.Handle.SizeOf}");
                case CXTypeKind.CXType_UShort:
                case CXTypeKind.CXType_UInt:
                case CXTypeKind.CXType_ULong:
                case CXTypeKind.CXType_ULongLong:
                    return GetUnsignedIntegerName(builtinType.Handle.SizeOf, $"INVALID_UNSIGNED_INTEGER_{builtinType.Kind}_SIZEOF_{builtinType.Handle.SizeOf}");
                case CXTypeKind.CXType_Invalid:
                case CXTypeKind.CXType_Unexposed:
                case CXTypeKind.CXType_UInt128:
                case CXTypeKind.CXType_Int128:
                case CXTypeKind.CXType_NullPtr:
                case CXTypeKind.CXType_Overload:
                case CXTypeKind.CXType_Dependent:
                case CXTypeKind.CXType_ObjCId:
                case CXTypeKind.CXType_ObjCClass:
                case CXTypeKind.CXType_ObjCSel:
                case CXTypeKind.CXType_Float128:
                case CXTypeKind.CXType_Half:
                case CXTypeKind.CXType_Float16:
                case CXTypeKind.CXType_ShortAccum:
                case CXTypeKind.CXType_Accum:
                case CXTypeKind.CXType_LongAccum:
                case CXTypeKind.CXType_UShortAccum:
                case CXTypeKind.CXType_UAccum:
                case CXTypeKind.CXType_ULongAccum:
                case CXTypeKind.CXType_BFloat16:
                case CXTypeKind.CXType_Ibm128:
                case CXTypeKind.CXType_Complex:
                case CXTypeKind.CXType_Pointer:
                case CXTypeKind.CXType_BlockPointer:
                case CXTypeKind.CXType_LValueReference:
                case CXTypeKind.CXType_RValueReference:
                case CXTypeKind.CXType_Record:
                case CXTypeKind.CXType_Enum:
                case CXTypeKind.CXType_Typedef:
                case CXTypeKind.CXType_ObjCInterface:
                case CXTypeKind.CXType_ObjCObjectPointer:
                case CXTypeKind.CXType_FunctionNoProto:
                case CXTypeKind.CXType_FunctionProto:
                case CXTypeKind.CXType_ConstantArray:
                case CXTypeKind.CXType_Vector:
                case CXTypeKind.CXType_IncompleteArray:
                case CXTypeKind.CXType_VariableArray:
                case CXTypeKind.CXType_DependentSizedArray:
                case CXTypeKind.CXType_MemberPointer:
                case CXTypeKind.CXType_Auto:
                case CXTypeKind.CXType_Elaborated:
                case CXTypeKind.CXType_Pipe:
                case CXTypeKind.CXType_OCLImage1dRO:
                case CXTypeKind.CXType_OCLImage1dArrayRO:
                case CXTypeKind.CXType_OCLImage1dBufferRO:
                case CXTypeKind.CXType_OCLImage2dRO:
                case CXTypeKind.CXType_OCLImage2dArrayRO:
                case CXTypeKind.CXType_OCLImage2dDepthRO:
                case CXTypeKind.CXType_OCLImage2dArrayDepthRO:
                case CXTypeKind.CXType_OCLImage2dMSAARO:
                case CXTypeKind.CXType_OCLImage2dArrayMSAARO:
                case CXTypeKind.CXType_OCLImage2dMSAADepthRO:
                case CXTypeKind.CXType_OCLImage2dArrayMSAADepthRO:
                case CXTypeKind.CXType_OCLImage3dRO:
                case CXTypeKind.CXType_OCLImage1dWO:
                case CXTypeKind.CXType_OCLImage1dArrayWO:
                case CXTypeKind.CXType_OCLImage1dBufferWO:
                case CXTypeKind.CXType_OCLImage2dWO:
                case CXTypeKind.CXType_OCLImage2dArrayWO:
                case CXTypeKind.CXType_OCLImage2dDepthWO:
                case CXTypeKind.CXType_OCLImage2dArrayDepthWO:
                case CXTypeKind.CXType_OCLImage2dMSAAWO:
                case CXTypeKind.CXType_OCLImage2dArrayMSAAWO:
                case CXTypeKind.CXType_OCLImage2dMSAADepthWO:
                case CXTypeKind.CXType_OCLImage2dArrayMSAADepthWO:
                case CXTypeKind.CXType_OCLImage3dWO:
                case CXTypeKind.CXType_OCLImage1dRW:
                case CXTypeKind.CXType_OCLImage1dArrayRW:
                case CXTypeKind.CXType_OCLImage1dBufferRW:
                case CXTypeKind.CXType_OCLImage2dRW:
                case CXTypeKind.CXType_OCLImage2dArrayRW:
                case CXTypeKind.CXType_OCLImage2dDepthRW:
                case CXTypeKind.CXType_OCLImage2dArrayDepthRW:
                case CXTypeKind.CXType_OCLImage2dMSAARW:
                case CXTypeKind.CXType_OCLImage2dArrayMSAARW:
                case CXTypeKind.CXType_OCLImage2dMSAADepthRW:
                case CXTypeKind.CXType_OCLImage2dArrayMSAADepthRW:
                case CXTypeKind.CXType_OCLImage3dRW:
                case CXTypeKind.CXType_OCLSampler:
                case CXTypeKind.CXType_OCLEvent:
                case CXTypeKind.CXType_OCLQueue:
                case CXTypeKind.CXType_OCLReserveID:
                case CXTypeKind.CXType_ObjCObject:
                case CXTypeKind.CXType_ObjCTypeParam:
                case CXTypeKind.CXType_Attributed:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCMcePayload:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCImePayload:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCRefPayload:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCSicPayload:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCMceResult:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCImeResult:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCRefResult:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCSicResult:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCImeResultSingleRefStreamout:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCImeResultDualRefStreamout:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCImeSingleRefStreamin:
                case CXTypeKind.CXType_OCLIntelSubgroupAVCImeDualRefStreamin:
                case CXTypeKind.CXType_ExtVector:
                case CXTypeKind.CXType_Atomic:
                case CXTypeKind.CXType_BTFTagAttributed:
                default:
                    return $"INVALID_BUILTIN_{builtinType.Kind}";
            }
        }

        if (type is ConstantArrayType constantArrayType)
            return GetTypeName(constantArrayType.ElementType);

        if (type is ElaboratedType elaboratedType)
        {
            return elaboratedType.AsString switch
            {
                "size_t" => "System.IntPtr",
                "va_list" => "void*",
                _ => GetTypeName(elaboratedType.CanonicalType)
            };
        }

        if (type is EnumType enumType)
            return GetCursorName(enumType.Decl);

        if (type is FunctionProtoType functionProtoType)
            return !_options.GenerateFunctionPointers ? "System.IntPtr" : GetCSharpFunctionPointer(functionProtoType);

        if (type is IncompleteArrayType incompleteArrayType)
            return GetTypeName(incompleteArrayType.ElementType) + "*";

        if (type is PointerType pointerType)
        {
            if (pointerType.PointeeType is FunctionProtoType)
                return GetTypeName(pointerType.PointeeType);

            return GetTypeName(pointerType.PointeeType) + "*";
        }

        if (type is RecordType recordType)
            return GetCursorName(recordType.Decl);

        return "UNHANDLED_TYPE";
    }

    private static string GetValidIdentifier(string identifier, bool remap = true)
    {
        if (remap)
        {
            foreach ((string prefix, string replacement) in _options.RemappedPrefixes)
            {
                if (!identifier.StartsWith(prefix, StringComparison.InvariantCulture))
                    continue;

                identifier = replacement + identifier[prefix.Length..];
                break;
            }
        }

        switch (identifier)
        {
            case "abstract":
            case "as":
            case "base":
            case "bool":
            case "break":
            case "byte":
            case "case":
            case "catch":
            case "char":
            case "checked":
            case "class":
            case "const":
            case "continue":
            case "decimal":
            case "default":
            case "delegate":
            case "do":
            case "double":
            case "else":
            case "enum":
            case "event":
            case "explicit":
            case "extern":
            case "false":
            case "finally":
            case "fixed":
            case "float":
            case "for":
            case "foreach":
            case "goto":
            case "if":
            case "implicit":
            case "in":
            case "int":
            case "interface":
            case "internal":
            case "is":
            case "lock":
            case "long":
            case "namespace":
            case "new":
            case "null":
            case "object":
            case "operator":
            case "out":
            case "override":
            case "params":
            case "private":
            case "protected":
            case "public":
            case "readonly":
            case "ref":
            case "return":
            case "sbyte":
            case "sealed":
            case "short":
            case "sizeof":
            case "stackalloc":
            case "static":
            case "string":
            case "struct":
            case "switch":
            case "this":
            case "throw":
            case "true":
            case "try":
            case "typeof":
            case "uint":
            case "ulong":
            case "unchecked":
            case "unsafe":
            case "ushort":
            case "using":
            case "virtual":
            case "void":
            case "volatile":
            case "while":
                return "@" + identifier;
            default:
                return identifier;
        }
    }
}
