﻿using Bindgen.NET;

const string exampleSource = """
// Bindgen.NET has clang headers built-in
#include <stdint.h>
#include <stdbool.h>

// Structs
typedef struct {
    uint32_t IntField;
    uint32_t IntArray[4];
} ExampleStruct;

// Enums
typedef enum {
    Red,
    Green,
    Blue
} ExampleEnum;

// Functions
bool ExampleFunction(ExampleStruct ExampleParameter);

// Value-like macros
#define Five (5)
#define Ten (5 + Five)
#define World "World"
#define HelloWorld "Hello " World

// Extern variables
extern ExampleStruct ExternExampleStruct;
extern void* ExternPointer;
""";

BindingOptions exampleConfig = new()
{
    Namespace = "ExampleNamespace",
    Class = "ExampleClass",

    DllImportPath = "libexample",
    ExternVariableImportPath = "libexample",

    TreatInputFileAsRawSourceCode = true,
    InputFile = exampleSource,

    IncludeBuiltInClangHeaders = true,

    GenerateToFilesystem = false,
    GenerateFunctionPointers = true,
    GenerateMacros = true,
    GenerateExternVariables = true
};

string generatedSource = BindingGenerator.Generate(exampleConfig);

System.Console.WriteLine(generatedSource);

// Below is the resulting bindings
namespace ExampleNamespace
{
    public static unsafe partial class ExampleClass
    {
        public const string DllImportPath = "libexample";

        public static string ExternVariableImportPath { get; set; } = "libexample";

        [System.Runtime.InteropServices.DllImport(DllImportPath, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern byte ExampleFunction(ExampleStruct ExampleParameter);

        public partial struct ExampleStruct
        {
            public uint IntField;

            public IntArray_FixedBuffer IntArray;

            public partial struct IntArray_FixedBuffer
            {
                public uint Item0;

                public uint Item1;

                public uint Item2;

                public uint Item3;
            }
        }

        public enum ExampleEnum : uint
        {
            Red = 0,
            Green = 1,
            Blue = 2
        }

        public const ExampleEnum Red = ExampleEnum.Red;

        public const ExampleEnum Green = ExampleEnum.Green;

        public const ExampleEnum Blue = ExampleEnum.Blue;

        public const int Five = 5;

        public const string HelloWorld = "Hello World";

        public const int Ten = 10;

        public const string World = "World";

        private static void* ExternExampleStruct_Ptr;

        private static void* ExternPointer_Ptr;

        public static ref ExampleStruct ExternExampleStruct
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get
            {
                if (ExternExampleStruct_Ptr != null)
                    return ref *(ExampleStruct*)ExternExampleStruct_Ptr;
                @LoadExternVar("ExternExampleStruct", out ExternExampleStruct_Ptr);
                return ref *(ExampleStruct*)ExternExampleStruct_Ptr;
            }
        }

        public static ref void* ExternPointer
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get
            {
                if (ExternPointer_Ptr != null)
                    return ref *(void**)ExternPointer_Ptr;
                @LoadExternVar("ExternPointer", out ExternPointer_Ptr);
                return ref *(void**)ExternPointer_Ptr;
            }
        }

        private static void @LoadExternVar(string variableSymbol, out void* field)
        {
            if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(ExternVariableImportPath, out System.IntPtr libraryHandle))
                throw new System.NullReferenceException($"Failed to load \"{ExternVariableImportPath}\" native library.");
            if (!System.Runtime.InteropServices.NativeLibrary.TryGetExport(libraryHandle, variableSymbol, out System.IntPtr variableHandle))
                throw new System.NullReferenceException($"Failed to load \"{variableSymbol}\" variable symbol.");
            field = (void*)variableHandle;
            System.Runtime.InteropServices.NativeLibrary.Free(libraryHandle);
        }
    }
}
