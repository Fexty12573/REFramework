#nullable enable

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.Emit;
using System.IO;
using System.Dynamic;
using System.Security.Cryptography;
using System.Linq;
using Microsoft.CodeAnalysis.Operations;
using REFrameworkNET;
using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ValueType = System.ValueType;

public class ClassGenerator {
    public class PseudoProperty {
        public REFrameworkNET.Method? getter;
        public REFrameworkNET.Method? setter;
        public REFrameworkNET.TypeDefinition? type;
        public bool indexer = false;
        public REFrameworkNET.TypeDefinition? indexType;
    };

    private Dictionary<string, PseudoProperty> pseudoProperties = [];

    private string className;
    private string actualName;
    private REFrameworkNET.TypeDefinition t;
    private List<REFrameworkNET.Method> methods = [];
    private List<REFrameworkNET.Field> fields = [];
    public List<REFrameworkNET.TypeDefinition> usingTypes = [];
    private TypeDeclarationSyntax? typeDeclaration;
    private bool addedNewKeyword = false;
    
    private List<FieldDeclarationSyntax> internalFieldDeclarations = [];

    private static string[] valueTypeMethods = ["Equals", "GetHashCode", "ToString"];

    public TypeDeclarationSyntax? TypeDeclaration {
        get {
            return typeDeclaration;
        }
    }

    public bool AddedNewKeyword {
        get {
            return addedNewKeyword;
        }
    }

    public void Update(TypeDeclarationSyntax? typeDeclaration_) {
        typeDeclaration = typeDeclaration_;
    }

    public ClassGenerator(string className_, REFrameworkNET.TypeDefinition t_) {
        className = REFrameworkNET.AssemblyGenerator.FixBadChars(className_);
        t = t_;

        foreach (var method in t_.Methods) {
            // Means we've entered the parent type
            if (method.DeclaringType != t_) {
                break;
            }
            
            if (method.Name == null) {
                continue;
            }
            
            if (method.ReturnType == null || method.ReturnType.FullName == null) {
                REFrameworkNET.API.LogError("Method " + method.Name + " has a null return type");
                continue;
            }

            if (method.Name.StartsWith("get_") && method.ReturnType.FullName != "System.Void") {
                if (method.Parameters.Count == 0) {
                    // Add the getter to the pseudo property (create if it doesn't exist)
                    var propertyName = method.Name[4..];
                    if (!pseudoProperties.ContainsKey(propertyName)) {
                        pseudoProperties[propertyName] = new PseudoProperty();
                    }

                    pseudoProperties[propertyName].getter = method;
                    pseudoProperties[propertyName].type = method.ReturnType;
                } else if (method.Parameters.Count == 1 && method.Name == "get_Item") {
                    // This is an indexer property
                    var propertyName = method.Name[4..];
                    if (!pseudoProperties.ContainsKey(propertyName)) {
                        pseudoProperties[propertyName] = new PseudoProperty();
                    }

                    pseudoProperties[propertyName].getter = method;
                    pseudoProperties[propertyName].type = method.ReturnType;
                    pseudoProperties[propertyName].indexer = true;
                    pseudoProperties[propertyName].indexType = method.Parameters[0].Type;
                }
            } else if (method.Name.StartsWith("set_")) {
                if (method.Parameters.Count == 1) {
                    // Add the setter to the pseudo property (create if it doesn't exist)
                    var propertyName = method.Name[4..];
                    if (!pseudoProperties.ContainsKey(propertyName)) {
                        pseudoProperties[propertyName] = new PseudoProperty();
                    }

                    pseudoProperties[propertyName].setter = method;
                    pseudoProperties[propertyName].type = method.Parameters[0].Type;
                } else if (method.Parameters.Count == 2 && method.Name == "set_Item") {
                    // This is an indexer property
                    var propertyName = method.Name[4..];
                    if (!pseudoProperties.ContainsKey(propertyName)) {
                        pseudoProperties[propertyName] = new PseudoProperty();
                    }

                    pseudoProperties[propertyName].setter = method;
                    pseudoProperties[propertyName].type = method.Parameters[1].Type;
                    pseudoProperties[propertyName].indexer = true;
                    pseudoProperties[propertyName].indexType = method.Parameters[0].Type;
                }
            } else {
                methods.Add(method);
            }
        }

        foreach (var field in t_.Fields) {
            // Means we've entered the parent type
            if (field.DeclaringType != t_) {
                break;
            }

            if (field.Name == null) {
                continue;
            }

            if (field.Type == null || field.Type.FullName == null) {
                REFrameworkNET.API.LogError("Field " + field.Name + " has a null field type");
                continue;
            }

            fields.Add(field);

            var fieldName = new string(field.Name);

            if (fieldName.StartsWith("<") && fieldName.EndsWith("k__BackingField")) {
                fieldName = fieldName[1..fieldName.IndexOf(">k__")];
            }

            // remove any methods that start with get/set_{field.Name}
            // because we're going to make them properties instead
            methods.RemoveAll(method => method.Name == "get_" + fieldName || method.Name == "set_" + fieldName);
            pseudoProperties.Remove(fieldName);
        }

        typeDeclaration = (t_.IsValueType() && !t_.IsEnum()) ? GenerateValueType() : Generate();
    }

    private static TypeSyntax MakeProperType(REFrameworkNET.TypeDefinition? targetType, REFrameworkNET.TypeDefinition? containingType) {
        TypeSyntax outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));

        string ogTargetTypename = targetType != null ? targetType.GetFullName() : "";
        string targetTypeName = REFrameworkNET.AssemblyGenerator.FixBadChars(ogTargetTypename);

        if (targetType == null || targetTypeName == "System.Void" || targetTypeName == "") {
            return outSyntax;
        }

        if (AssemblyGenerator.typeFullRenames.TryGetValue(targetType, out string? value)) {
            targetTypeName = value;
        }

        // Check for easily convertible types like System.Single, System.Int32, etc.
        switch (targetTypeName) { 
            case "System.Single":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword));
                break;
            case "System.Double":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.DoubleKeyword));
                break;
            case "System.Int32":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword));
                break;
            case "System.UInt32":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.UIntKeyword));
                break;
            case "System.Int16":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ShortKeyword));
                break;
            case "System.UInt16":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.UShortKeyword));
                break;
            case "System.Byte":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ByteKeyword));
                break;
            case "System.SByte":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.SByteKeyword));
                break;
            case "System.Char":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.CharKeyword));
                break;
            case "System.Int64":
            case "System.IntPtr":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.LongKeyword));
                break;
            case "System.UInt64":
            case "System.UIntPtr":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ULongKeyword));
                break;
            case "System.Boolean":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword));
                break;
            case "System.String":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword));
                break;
            case "via.clr.ManagedObject":
            case "System.Object":
                outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
                break;
            default:
                if (!REFrameworkNET.AssemblyGenerator.validTypes.Contains(ogTargetTypename)) {
                    outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
                    break;
                }

                /*if (targetTypeName.Contains('<')) {
                    outSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
                    break;
                }*/

                targetTypeName = "global::" + REFrameworkNET.AssemblyGenerator.CorrectTypeName(targetTypeName);

                outSyntax = SyntaxFactory.ParseTypeName(targetTypeName);
                break;
        }
        
        return outSyntax;
    }
    
    static readonly SortedSet<string> invalidMethodNames = [
        "Finalize",
        //"MemberwiseClone",
        //"ToString",
        //"Equals",
        //"GetHashCode",
        //"GetType",
        ".ctor",
        ".cctor",
        /*"op_Implicit",
        "op_Explicit",
        "op_Addition",
        "op_Subtraction",
        "op_Multiply",
        "op_Division",
        "op_Modulus",
        "op_BitwiseAnd",
        "op_BitwiseOr",
        "op_ExclusiveOr",*/
        
    ];

    private TypeDeclarationSyntax? Generate() {
        usingTypes = [];

        var ogClassName = new string(className);

        // Pull out the last part of the class name (split '.' till last)
        if (t.DeclaringType == null) {
            className = className.Split('.').Last();
        }

        actualName = REFrameworkNET.AssemblyGenerator.CorrectTypeName(className);
        typeDeclaration = SyntaxFactory
            .InterfaceDeclaration(actualName)
            .AddModifiers(new SyntaxToken[]{SyntaxFactory.Token(SyntaxKind.PublicKeyword)});

        if (typeDeclaration == null) {
            return null;
        }

        // Check if we need to add the new keyword to this.
        if (AssemblyGenerator.NestedTypeExistsInParent(t)) {
            typeDeclaration = typeDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword));
            addedNewKeyword = true;
        }

        // Set up base types
        List<SimpleBaseTypeSyntax> baseTypes = [];

        for (var parent = t.ParentType; parent != null; parent = parent.ParentType) {
            // TODO: Fix this
            if (!AssemblyGenerator.validTypes.Contains(parent.FullName)) {
                continue;
            }

            AssemblyGenerator.typeFullRenames.TryGetValue(parent, out string? parentName);
            parentName = AssemblyGenerator.CorrectTypeName(parentName ?? parent.FullName ?? "");

            if (parentName == null) {
                break;
            }

            if (parentName == "") {
                break;
            }

            if (parentName.Contains('[')) {
                break;
            }

            // Forces compiler to start at the global namespace
            parentName = "global::" + parentName;

            baseTypes.Add(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(parentName)));
            usingTypes.Add(parent);
            break;
        }

        // Add a static field to the class that holds the REFrameworkNET.TypeDefinition
        var refTypeVarDecl = SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("global::REFrameworkNET.TypeDefinition"))
            .AddVariables(SyntaxFactory.VariableDeclarator("REFType").WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression("global::REFrameworkNET.TDB.Get().FindType(\"" + t.FullName + "\")"))));
        
        var refTypeFieldDecl = SyntaxFactory.FieldDeclaration(refTypeVarDecl).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        // Add a static field that holds a NativeProxy to the class (for static methods)
        var refProxyVarDecl = SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(REFrameworkNET.AssemblyGenerator.CorrectTypeName(className)))
            .AddVariables(SyntaxFactory.VariableDeclarator("REFProxy").WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression("REFType.As<" + REFrameworkNET.AssemblyGenerator.CorrectTypeName(t.FullName) + ">()"))));

        var refProxyFieldDecl = SyntaxFactory.FieldDeclaration(refProxyVarDecl).AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        typeDeclaration = GenerateMethods(baseTypes);
        typeDeclaration = GenerateFields(baseTypes);
        typeDeclaration = GenerateProperties(baseTypes);

        if (baseTypes.Count > 0 && typeDeclaration != null) {
            refTypeFieldDecl = refTypeFieldDecl.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword));
            //fieldDeclaration2 = fieldDeclaration2.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword));

            typeDeclaration = (typeDeclaration as InterfaceDeclarationSyntax)?.AddBaseListTypes(baseTypes.ToArray());
        }

        if (typeDeclaration != null) {
            typeDeclaration = typeDeclaration.AddMembers(refTypeFieldDecl);
            //typeDeclaration = typeDeclaration.AddMembers(refProxyFieldDecl);
        }

        // Logically needs to come after the REFType field is added as they reference it
        if (internalFieldDeclarations.Count > 0 && typeDeclaration != null) {
            typeDeclaration = typeDeclaration.AddMembers(internalFieldDeclarations.ToArray());
        }

        return GenerateNestedTypes();
    }

    private TypeDeclarationSyntax? GenerateValueType() {
        usingTypes = [];

        var ogClassName = new string(className);

        // Pull out the last part of the class name (split '.' till last)
        if (t.DeclaringType == null) {
            className = className.Split('.').Last();
        }

        actualName = REFrameworkNET.AssemblyGenerator.CorrectTypeName(className);
        typeDeclaration = SyntaxFactory
            .StructDeclaration(actualName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

        if (typeDeclaration == null) {
            return null;
        }

        // Add a static field to the class that holds the REFrameworkNET.TypeDefinition
        var refTypeVarDecl = SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("global::REFrameworkNET.TypeDefinition"))
            .AddVariables(SyntaxFactory.VariableDeclarator("REFType").WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression("global::REFrameworkNET.TDB.Get().FindType(\"" + t.FullName + "\")"))));

        var refTypeFieldDecl = SyntaxFactory.FieldDeclaration(refTypeVarDecl).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        // Add a static field that holds a NativeProxy to the class (for static methods)
        var refProxyVarDecl = SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(REFrameworkNET.AssemblyGenerator.CorrectTypeName(className)))
            .AddVariables(SyntaxFactory.VariableDeclarator("REFProxy").WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression("REFType.As<" + REFrameworkNET.AssemblyGenerator.CorrectTypeName(t.FullName) + ">()"))));

        var refProxyFieldDecl = SyntaxFactory.FieldDeclaration(refProxyVarDecl).AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        var structLayoutAttr = SyntaxFactory.AttributeList().AddAttributes(SyntaxFactory.Attribute(SyntaxFactory.ParseName("global::System.Runtime.InteropServices.StructLayout"), SyntaxFactory.ParseAttributeArgumentList("(global::System.Runtime.InteropServices.LayoutKind.Explicit)")));

        typeDeclaration = typeDeclaration.AddAttributeLists(structLayoutAttr);

        typeDeclaration = GenerateValueTypeFields();
        typeDeclaration = GenerateValueTypeMethods();
        typeDeclaration = GenerateProperties([]);

        typeDeclaration = typeDeclaration?.AddMembers(refTypeFieldDecl);

        // Logically needs to come after the REFType field is added as they reference it
        if (internalFieldDeclarations.Count > 0 && typeDeclaration != null) {
            typeDeclaration = typeDeclaration.AddMembers(internalFieldDeclarations.ToArray());
        }

        return GenerateNestedTypes();
    }

    private TypeDeclarationSyntax GenerateProperties(List<SimpleBaseTypeSyntax> baseTypes) {
        if (typeDeclaration == null) {
            throw new Exception("Type declaration is null"); // This should never happen
        }

        if (pseudoProperties.Count == 0) {
            return typeDeclaration!;
        }

        var matchingProperties = pseudoProperties
            .Select(property => {
                var propertyType = MakeProperType(property.Value.type, t);
                var propertyName = new string(property.Key);

                BasePropertyDeclarationSyntax propertyDeclaration = SyntaxFactory.PropertyDeclaration(propertyType, propertyName)
                    .AddModifiers([SyntaxFactory.Token(SyntaxKind.PublicKeyword)]);

                if (property.Value.indexer) {
                    ParameterSyntax parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier("index")).WithType(MakeProperType(property.Value.indexType, t));

                    propertyDeclaration = SyntaxFactory.IndexerDeclaration(propertyType)
                        .AddModifiers([SyntaxFactory.Token(SyntaxKind.PublicKeyword)])
                        .AddParameterListParameters(parameter);
                }

                bool shouldAddNewKeyword = false;
                bool shouldAddStaticKeyword = false;

                if (property.Value.getter != null) {
                    var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                        .AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(SyntaxFactory.Attribute(
                            SyntaxFactory.ParseName("global::REFrameworkNET.Attributes.Method"),
                            SyntaxFactory.ParseAttributeArgumentList("(" + property.Value.getter.Index.ToString() + ", global::REFrameworkNET.FieldFacadeType.None)"))
                        ));

                    if (property.Value.getter.IsStatic()) {
                        shouldAddStaticKeyword = true;

                        // Now we must add a body to it that actually calls the method
                        // We have our REFType field, so we can lookup the method and call it
                        // Make a private static field to hold the REFrameworkNET.Method
                        var internalFieldName = "INTERNAL_" + propertyName + property.Value.getter.Index.ToString();
                        var methodVariableDeclaration = SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("global::REFrameworkNET.Method"))
                            .AddVariables(SyntaxFactory.VariableDeclarator(internalFieldName).WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression("REFType.GetMethod(\"" + property.Value.getter.GetMethodSignature() + "\")"))));

                        var methodFieldDeclaration = SyntaxFactory.FieldDeclaration(methodVariableDeclaration).AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
                        internalFieldDeclarations.Add(methodFieldDeclaration);

                        List<StatementSyntax> bodyStatements = [];
                        bodyStatements.Add(SyntaxFactory.ParseStatement("return (" + propertyType.GetText().ToString() + ")" + internalFieldName + ".InvokeBoxed(typeof(" + propertyType.GetText().ToString() + "), null, null);"));

                        getter = getter.AddBodyStatements(bodyStatements.ToArray());
                    } else {
                        getter = getter.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                    }

                    propertyDeclaration = propertyDeclaration.AddAccessorListAccessors(getter);
                    
                    var getterExtension = Il2CppDump.GetMethodExtension(property.Value.getter);

                    if (baseTypes.Count > 0 && getterExtension != null && getterExtension.Override != null && getterExtension.Override == true) {
                        var matchingParentMethods = getterExtension.MatchingParentMethods;

                        // Go through the parents, check if the parents are allowed to be generated
                        // and add the new keyword if the matching method is found in one allowed to be generated
                        foreach (var matchingMethod in matchingParentMethods) {
                            var parent = matchingMethod.DeclaringType;
                            if (!REFrameworkNET.AssemblyGenerator.validTypes.Contains(parent.FullName)) {
                                continue;
                            }

                            shouldAddNewKeyword = true;
                            break;
                        }
                    }

                    if (baseTypes.Count > 0 && !shouldAddNewKeyword) {
                        var declaringType = property.Value.getter.DeclaringType;
                        if (declaringType != null) {
                            var parent = declaringType.ParentType;

                            if (parent != null && (parent.FindField(propertyName) != null || parent.FindField("<" + propertyName + ">k__BackingField") != null)) {
                                shouldAddNewKeyword = true;
                            }
                        }
                    }
                }

                if (property.Value.setter != null) {
                    var setter = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                        .AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(SyntaxFactory.Attribute(
                            SyntaxFactory.ParseName("global::REFrameworkNET.Attributes.Method"),
                            SyntaxFactory.ParseAttributeArgumentList("(" + property.Value.setter.Index.ToString() + ", global::REFrameworkNET.FieldFacadeType.None)"))
                        ));
                    
                    if (property.Value.setter.IsStatic()) {
                        shouldAddStaticKeyword = true;

                        // Now we must add a body to it that actually calls the method
                        // We have our REFType field, so we can lookup the method and call it
                        // Make a private static field to hold the REFrameworkNET.Method
                        var internalFieldName = "INTERNAL_" + propertyName + property.Value.setter.Index.ToString();
                        var methodVariableDeclaration = SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("global::REFrameworkNET.Method"))
                            .AddVariables(SyntaxFactory.VariableDeclarator(internalFieldName).WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression("REFType.GetMethod(\"" + property.Value.setter.GetMethodSignature() + "\")"))));

                        var methodFieldDeclaration = SyntaxFactory.FieldDeclaration(methodVariableDeclaration).AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
                        internalFieldDeclarations.Add(methodFieldDeclaration);

                        List<StatementSyntax> bodyStatements = [];
                        bodyStatements.Add(SyntaxFactory.ParseStatement(internalFieldName + ".Invoke(null, new object[] {value});"));

                        setter = setter.AddBodyStatements(bodyStatements.ToArray());
                    } else {
                        setter = setter.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                    }
                    
                    propertyDeclaration = propertyDeclaration.AddAccessorListAccessors(setter);

                    var setterExtension = Il2CppDump.GetMethodExtension(property.Value.setter);

                    if (baseTypes.Count > 0 && setterExtension != null && setterExtension.Override != null && setterExtension.Override == true) {
                        var matchingParentMethods = setterExtension.MatchingParentMethods;

                        // Go through the parents, check if the parents are allowed to be generated
                        // and add the new keyword if the matching method is found in one allowed to be generated
                        foreach (var matchingMethod in matchingParentMethods) {
                            var parent = matchingMethod.DeclaringType;
                            if (!REFrameworkNET.AssemblyGenerator.validTypes.Contains(parent.FullName)) {
                                continue;
                            }

                            shouldAddNewKeyword = true;
                            break;
                        }
                    }

                    if (baseTypes.Count > 0 && !shouldAddNewKeyword) {
                        var declaringType = property.Value.setter.DeclaringType;
                        if (declaringType != null) {
                            var parent = declaringType.ParentType;

                            if (parent != null && (parent.FindField(propertyName) != null || parent.FindField("<" + propertyName + ">k__BackingField") != null)) {
                                shouldAddNewKeyword = true;
                            }
                        }
                    }
                }

                if (shouldAddStaticKeyword) {
                    propertyDeclaration = propertyDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
                }

                if (shouldAddNewKeyword) {
                    propertyDeclaration = propertyDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword));
                }

                return propertyDeclaration;
            });

        return typeDeclaration.AddMembers(matchingProperties.ToArray());
    }

    private List<Field> GetValidFields()
    {
        List<REFrameworkNET.Field> validFields = [];

        int totalFields = 0;

        foreach (var field in fields)
        {
            if (field == null)
            {
                continue;
            }

            if (field.Name == null)
            {
                continue;
            }

            if (field.Type == null)
            {
                continue;
            }

            if (field.Type.FullName == null)
            {
                continue;
            }

            if (field.Type.FullName.Contains('!'))
            {
                continue;
            }

            // Make sure field name only contains ASCII characters
            if (field.Name.Any(c => c > 127))
            {
                System.Console.WriteLine("Skipping field with non-ASCII characters: " + field.Name + " " + field.Index);
                continue;
            }

            // We don't want any of the properties to be "void" properties
            if (!REFrameworkNET.AssemblyGenerator.validTypes.Contains(field.Type.FullName))
            {
                continue;
            }

            ++totalFields;

            validFields.Add(field);

            // Some kind of limitation in the runtime prevents too many methods in the class
            if (totalFields >= (ushort.MaxValue - 15) / 2)
            {
                System.Console.WriteLine("Skipping fields in " + t.FullName + " because it has too many fields (" + fields.Count + ")");
                break;
            }
        }

        return validFields;
    }

    private TypeDeclarationSyntax GenerateFields(List<SimpleBaseTypeSyntax> baseTypes) {
        if (typeDeclaration == null) {
            throw new Exception("Type declaration is null"); // This should never happen
        }

        if (fields.Count == 0) {
            return typeDeclaration!;
        }

        var validFields = GetValidFields();

        var matchingFields = validFields
            .Select(field => {
                var fieldType = MakeProperType(field.Type, t);
                var fieldName = new string(field.Name);

                // Replace the k backingfield crap
                if (fieldName.StartsWith("<") && fieldName.EndsWith("k__BackingField")) {
                    fieldName = fieldName[1..fieldName.IndexOf(">k__")];
                }

                // So this is actually going to be made a property with get/set instead of an actual field
                // 1. Because interfaces can't have fields
                // 2. Because we don't actually have a concrete reference to the field in our VM, so we'll be a facade for the field
                var fieldFacadeGetter = SyntaxFactory.AttributeList().AddAttributes(SyntaxFactory.Attribute(
                    SyntaxFactory.ParseName("global::REFrameworkNET.Attributes.Method"),
                    SyntaxFactory.ParseAttributeArgumentList($"({field.Index}, global::REFrameworkNET.FieldFacadeType.Getter)"))
                );

                var fieldFacadeSetter = SyntaxFactory.AttributeList().AddAttributes(SyntaxFactory.Attribute(
                    SyntaxFactory.ParseName("global::REFrameworkNET.Attributes.Method"),
                    SyntaxFactory.ParseAttributeArgumentList($"({field.Index}, global::REFrameworkNET.FieldFacadeType.Setter)"))
                );

                AccessorDeclarationSyntax getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).AddAttributeLists(fieldFacadeGetter);
                AccessorDeclarationSyntax setter = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).AddAttributeLists(fieldFacadeSetter);

                var propertyDeclaration = SyntaxFactory.PropertyDeclaration(fieldType, fieldName)
                    .AddModifiers([SyntaxFactory.Token(SyntaxKind.PublicKeyword)]);

                if (field.IsStatic()) {
                    propertyDeclaration = propertyDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword));

                    // Now we must add a body to it that actually calls the method
                    // We have our REFType field, so we can lookup the method and call it
                    // Make a private static field to hold the REFrameworkNET.Method
                    var internalFieldName = "INTERNAL_" + fieldName + field.GetIndex().ToString();
                    var fieldVariableDeclaration = SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("global::REFrameworkNET.Field"))
                        .AddVariables(SyntaxFactory.VariableDeclarator(internalFieldName).WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression($"REFType.GetField(\"{field.GetName()}\")"))));

                    var fieldDeclaration = SyntaxFactory.FieldDeclaration(fieldVariableDeclaration).AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
                    internalFieldDeclarations.Add(fieldDeclaration);

                    List<StatementSyntax> bodyStatementsSetter = [];
                    List<StatementSyntax> bodyStatementsGetter = [];


                    bodyStatementsGetter.Add(SyntaxFactory.ParseStatement("return (" + fieldType.GetText().ToString() + ")" + internalFieldName + ".GetDataBoxed(typeof(" + fieldType.GetText().ToString() + "), 0, false);"));
                    bodyStatementsSetter.Add(SyntaxFactory.ParseStatement(internalFieldName + ".SetDataBoxed(0, new object[] {value}, false);"));

                    getter = getter.AddBodyStatements(bodyStatementsGetter.ToArray());
                    setter = setter.AddBodyStatements(bodyStatementsSetter.ToArray());
                } else {
                    getter = getter.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                    setter = setter.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                }

                propertyDeclaration = propertyDeclaration.AddAccessorListAccessors(getter, setter);

                // Search for k__BackingField version and the corrected version
                if (this.t.ParentType != null) {
                    var matchingField = this.t.ParentType.FindField(fieldName);
                    matchingField ??= this.t.ParentType.FindField(field.Name);
                    var matchingMethod = this.t.ParentType.FindMethod("get_" + fieldName);
                    matchingMethod ??= this.t.ParentType.FindMethod("set_" + fieldName);

                    bool added = false;

                    if (matchingField != null) {
                        var parentT = matchingField.DeclaringType;

                        if (parentT != null && REFrameworkNET.AssemblyGenerator.validTypes.Contains(parentT.FullName)) {
                            propertyDeclaration = propertyDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword));
                            added = true;
                        }
                    }

                    if (!added && matchingMethod != null) {
                        var parentT = matchingMethod.DeclaringType;

                        if (parentT != null && REFrameworkNET.AssemblyGenerator.validTypes.Contains(parentT.FullName)) {
                            propertyDeclaration = propertyDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword));
                        }
                    }

                    /*if ((this.t.ParentType.FindField(field.Name) != null || this.t.ParentType.FindField(fieldName) != null) || 
                        (this.t.ParentType.FindMethod("get_" + fieldName) != null || this.t.ParentType.FindMethod("set_" + fieldName) != null))
                    {
                        //propertyDeclaration = propertyDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword));
                    }*/
                }

                /*var fieldExtension = Il2CppDump.GetFieldExtension(field);

                if (fieldExtension != null && fieldExtension.MatchingParentFields.Count > 0) {
                    var matchingParentFields = fieldExtension.MatchingParentFields;

                    // Go through the parents, check if the parents are allowed to be generated
                    // and add the new keyword if the matching field is found in one allowed to be generated
                    foreach (var matchingField in matchingParentFields) {
                        var parent = matchingField.DeclaringType;
                        if (parent == null) {
                            continue;
                        }
                        if (!REFrameworkNET.AssemblyGenerator.validTypes.Contains(parent.FullName)) {
                            continue;
                        }

                        propertyDeclaration = propertyDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword));
                        break;
                    }
                }*/

                return propertyDeclaration;
            });

        return typeDeclaration.AddMembers(matchingFields.ToArray());
    }

    private TypeDeclarationSyntax GenerateValueTypeFields() {
        if (typeDeclaration == null) {
            throw new Exception("Type declaration is null"); // This should never happen
        }

        if (fields.Count == 0) {
            return typeDeclaration!;
        }

        var validFields = GetValidFields();

        var fieldOffsetAttrName = SyntaxFactory.ParseName("global::System.Runtime.InteropServices.FieldOffset");

        var matchingFields = validFields
            .Where(field => !field.IsStatic())
            .Select(field => {
                var fieldType = MakeProperType(field.Type, t);
                var fieldName = new string(field.Name);

                // Replace the k backingfield crap
                if (fieldName.StartsWith("<") && fieldName.EndsWith("k__BackingField"))
                {
                    fieldName = fieldName[1..fieldName.IndexOf(">k__")];
                }

                // Value types are generated with actual fields instead of properties
                var fieldDeclaration = SyntaxFactory.FieldDeclaration(
                        SyntaxFactory.VariableDeclaration(fieldType)
                            .AddVariables(SyntaxFactory.VariableDeclarator(fieldName)))
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

                fieldDeclaration = fieldDeclaration.AddAttributeLists(
                    SyntaxFactory.AttributeList().AddAttributes(
                        SyntaxFactory.Attribute(
                            fieldOffsetAttrName, 
                            SyntaxFactory.ParseAttributeArgumentList($"({field.OffsetFromFieldPtr})")
                        )
                    )
                );

                return fieldDeclaration;
            });

        return typeDeclaration.AddMembers(matchingFields.ToArray());
    }

    private static readonly Dictionary<string, SyntaxToken> operatorTokens = new() {
        ["op_Addition"] = SyntaxFactory.Token(SyntaxKind.PlusToken),
        ["op_UnaryPlus"] = SyntaxFactory.Token(SyntaxKind.PlusToken),
        ["op_Subtraction"] = SyntaxFactory.Token(SyntaxKind.MinusToken),
        ["op_UnaryNegation"] = SyntaxFactory.Token(SyntaxKind.MinusToken),
        ["op_Multiply"] = SyntaxFactory.Token(SyntaxKind.AsteriskToken),
        ["op_Division"] = SyntaxFactory.Token(SyntaxKind.SlashToken),
        ["op_Modulus"] = SyntaxFactory.Token(SyntaxKind.PercentToken),
        ["op_BitwiseAnd"] = SyntaxFactory.Token(SyntaxKind.AmpersandToken),
        ["op_BitwiseOr"] = SyntaxFactory.Token(SyntaxKind.BarToken),
        ["op_ExclusiveOr"] = SyntaxFactory.Token(SyntaxKind.CaretToken),
        ["op_LeftShift"] = SyntaxFactory.Token(SyntaxKind.LessThanLessThanToken),
        ["op_RightShift"] = SyntaxFactory.Token(SyntaxKind.GreaterThanGreaterThanToken),
        ["op_Equality"] = SyntaxFactory.Token(SyntaxKind.EqualsEqualsToken),
        ["op_Inequality"] = SyntaxFactory.Token(SyntaxKind.ExclamationEqualsToken),
        ["op_LessThan"] = SyntaxFactory.Token(SyntaxKind.LessThanToken),
        ["op_LessThanOrEqual"] = SyntaxFactory.Token(SyntaxKind.LessThanEqualsToken),
        ["op_GreaterThan"] = SyntaxFactory.Token(SyntaxKind.GreaterThanToken),
        ["op_GreaterThanOrEqual"] = SyntaxFactory.Token(SyntaxKind.GreaterThanEqualsToken),
        ["op_LogicalNot"] = SyntaxFactory.Token(SyntaxKind.ExclamationToken),
        ["op_OnesComplement"] = SyntaxFactory.Token(SyntaxKind.TildeToken),
        ["op_True"] = SyntaxFactory.Token(SyntaxKind.TrueKeyword),
        ["op_False"] = SyntaxFactory.Token(SyntaxKind.FalseKeyword),
        ["op_Implicit"] = SyntaxFactory.Token(SyntaxKind.ImplicitKeyword),
        ["op_Explicit"] = SyntaxFactory.Token(SyntaxKind.ExplicitKeyword),
    };

    private List<REFrameworkNET.Method> GetValidMethods() {
        List<REFrameworkNET.Method> validMethods = [];

        try {
            foreach(var m in methods) {
                if (m == null) {
                    continue;
                }

                if (invalidMethodNames.Contains(m.Name)) {
                    continue;
                }

                if (m.Name.Contains('<')) {
                    continue;
                }

                if (m.ReturnType.FullName.Contains('!')) {
                    continue;
                }

                validMethods.Add(m);
            }
        } catch (Exception e) {
            Console.WriteLine("ASDF Error: " + e.Message);
        }

        return validMethods;
    }

    private TypeDeclarationSyntax GenerateMethods(List<SimpleBaseTypeSyntax> baseTypes) {
        return GenerateMethods(baseTypes, []);
    }

    private TypeDeclarationSyntax GenerateMethods(List<SimpleBaseTypeSyntax> baseTypes, string[] explicitNew) {
        if (typeDeclaration == null) {
            throw new Exception("Type declaration is null"); // This should never happen
        }

        if (methods.Count == 0) {
            return typeDeclaration!;
        }

        HashSet<string> seenMethodSignatures = [];

        var validMethods = GetValidMethods();

        var matchingMethods = validMethods
            .Select(method => 
        {
            var returnType = MakeProperType(method.ReturnType, t);

            //string simpleMethodSignature = returnType.GetText().ToString();
            string simpleMethodSignature = ""; // Return types are not part of the signature. Return types are not overloaded.
            
            var methodName = new string(method.Name);
            var methodExtension = Il2CppDump.GetMethodExtension(method);

            // Hacky fix for MHR because parent classes have the same method names
            // while we support that, we don't support constructed generic arguments yet, they are just "object"
            if (methodName == "sortCountList") {
                Console.WriteLine("Skipping sortCountList");
                return null;
            }

            var methodDeclaration = SyntaxFactory.MethodDeclaration(returnType, methodName ?? "UnknownMethod")
                .AddModifiers(new SyntaxToken[]{SyntaxFactory.Token(SyntaxKind.PublicKeyword)})
                /*.AddBodyStatements(SyntaxFactory.ParseStatement("throw new System.NotImplementedException();"))*/;

            if (explicitNew.Contains(methodName)) {
                methodDeclaration = methodDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword));
            }

            if (operatorTokens.ContainsKey(methodName ?? "UnknownMethod")) {
                // Add SpecialName attribute to the method
                methodDeclaration = methodDeclaration.AddAttributeLists(
                    SyntaxFactory.AttributeList().AddAttributes(SyntaxFactory.Attribute(
                        SyntaxFactory.ParseName("global::System.Runtime.CompilerServices.SpecialName"))
                    )
                );
            }

            simpleMethodSignature += methodName;

            // Add full method name as a MethodName attribute to the method
            methodDeclaration = methodDeclaration.AddAttributeLists(
                SyntaxFactory.AttributeList().AddAttributes(SyntaxFactory.Attribute(
                    SyntaxFactory.ParseName("global::REFrameworkNET.Attributes.Method"),
                    SyntaxFactory.ParseAttributeArgumentList("(" + method.GetIndex().ToString() + ", global::REFrameworkNET.FieldFacadeType.None)")))
                );

            bool anyOutParams = false;
            System.Collections.Generic.List<string> paramNames = [];

            if (method.Parameters.Count > 0) {
                // If any of the params have ! in them, skip this method
                if (method.Parameters.Any(param => param != null && (param.Type == null || (param.Type != null && param.Type.FullName.Contains('!'))))) {
                    return null;
                }

                var runtimeMethod = method.GetRuntimeMethod();
                
                if (runtimeMethod == null) {
                    REFrameworkNET.API.LogWarning("Method " + method.DeclaringType.FullName + "." + method.Name + " has a null runtime method");
                    return null;
                }

                var runtimeParams = runtimeMethod.Call("GetParameters") as REFrameworkNET.ManagedObject;

                System.Collections.Generic.List<ParameterSyntax> parameters = [];

                bool anyUnsafeParams = false;

                if (runtimeParams != null) {
                    var methodActualRetval = method.GetReturnType();
                    UInt32 unknownArgCount = 0;

                    foreach (dynamic param in runtimeParams) {
                        /*if (param.get_IsRetval() == true) {
                            continue;
                        }*/

                        var paramDef = (REFrameworkNET.TypeDefinition)param.GetTypeDefinition();
                        var paramName = param.get_Name();

                        if (paramName == null || paramName == "") {
                            //paramName = "UnknownParam";
                            paramName = "arg" + unknownArgCount.ToString();
                            ++unknownArgCount;
                        }

                        if (paramName == "object") {
                            paramName = "object_"; // object is a reserved keyword.
                        }

                        var paramType = param.get_ParameterType();

                        if (paramType == null) {
                            paramNames.Add(paramName);
                            parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(SyntaxFactory.ParseTypeName("object")));
                            continue;
                        }

                        var parsedParamName = new string(paramName as string);
                        
                        /*if (param.get_IsGenericParameter() == true) {
                            return null; // no generic parameters.
                        }*/

                        var isByRef = paramType.IsByRefImpl();
                        var isPointer = paramType.IsPointerImpl();
                        var isOut = paramDef != null && paramDef.FindMethod("get_IsOut") != null ? param.get_IsOut() : false;
                        var paramTypeDef = (REFrameworkNET.TypeDefinition)paramType.get_TypeHandle();

                        var paramTypeSyntax = MakeProperType(paramTypeDef, t);
                        
                        System.Collections.Generic.List<SyntaxToken> modifiers = [];

                        if (isOut == true) {
                            simpleMethodSignature += "out";
                            modifiers.Add(SyntaxFactory.Token(SyntaxKind.OutKeyword));
                            anyOutParams = true;
                        }

                        if (isByRef == true) {
                            // can only be either ref or out.
                            if (!isOut) {
                                simpleMethodSignature += "ref " + paramTypeSyntax.GetText().ToString();
                                modifiers.Add(SyntaxFactory.Token(SyntaxKind.RefKeyword));
                            }

                            parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(SyntaxFactory.ParseTypeName(paramTypeSyntax.ToString())).AddModifiers(modifiers.ToArray()));
                        } else if (isPointer == true) {
                            simpleMethodSignature += "ptr " + paramTypeSyntax.GetText().ToString();
                            parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(SyntaxFactory.ParseTypeName(paramTypeSyntax.ToString() + "*")).AddModifiers(modifiers.ToArray()));
                            anyUnsafeParams = true;

                            parsedParamName = "(global::System.IntPtr) " + parsedParamName;
                        } else {
                            simpleMethodSignature += paramTypeSyntax.GetText().ToString();
                            parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(paramTypeSyntax).AddModifiers(modifiers.ToArray()));
                        }

                        paramNames.Add(parsedParamName);
                    }

                    methodDeclaration = methodDeclaration.AddParameterListParameters([.. parameters]);

                    if (anyUnsafeParams) {
                        methodDeclaration = methodDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));
                    }
                }
            } else {
                simpleMethodSignature += "()";
            }

            if (method.IsStatic()) {
                // lets see what happens if we just make it static
                methodDeclaration = methodDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword));

                // Now we must add a body to it that actually calls the method
                // We have our REFType field, so we can lookup the method and call it
                // Make a private static field to hold the REFrameworkNET.Method
                var internalFieldName = "INTERNAL_" + method.Name + method.GetIndex().ToString();
                var methodVariableDeclaration = SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("global::REFrameworkNET.Method"))
                    .AddVariables(SyntaxFactory.VariableDeclarator(internalFieldName).WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression("REFType.GetMethod(\"" + method.GetMethodSignature() + "\")"))));

                var methodFieldDeclaration = SyntaxFactory.FieldDeclaration(methodVariableDeclaration).AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
                internalFieldDeclarations.Add(methodFieldDeclaration);

                List<StatementSyntax> bodyStatements = [];

                if (method.ReturnType.FullName == "System.Void") {
                    if (method.Parameters.Count == 0) {
                        bodyStatements.Add(SyntaxFactory.ParseStatement(internalFieldName + ".Invoke(null, null);"));
                    } else if (!anyOutParams) {
                        bodyStatements.Add(SyntaxFactory.ParseStatement(internalFieldName + ".Invoke(null, new object[] {" + string.Join(", ", paramNames) + "});"));
                    } else {
                        bodyStatements.Add(SyntaxFactory.ParseStatement("throw new System.NotImplementedException();")); // TODO: Implement this
                    }
                } else {
                    if (method.Parameters.Count == 0) {
                        bodyStatements.Add(SyntaxFactory.ParseStatement("return (" + returnType.GetText().ToString() + ")" + internalFieldName + ".InvokeBoxed(typeof(" + returnType.GetText().ToString() + "), null, null);"));
                    } else if (!anyOutParams) {
                        bodyStatements.Add(SyntaxFactory.ParseStatement("return (" + returnType.GetText().ToString() + ")" + internalFieldName + ".InvokeBoxed(typeof(" + returnType.GetText().ToString() + "), null, new object[] {" + string.Join(", ", paramNames) + "});"));
                    } else {
                        bodyStatements.Add(SyntaxFactory.ParseStatement("throw new System.NotImplementedException();")); // TODO: Implement this
                    }
                }

                methodDeclaration = methodDeclaration.AddBodyStatements(
                    [.. bodyStatements]
                );
            }

            if (seenMethodSignatures.Contains(simpleMethodSignature)) {
                Console.WriteLine("Skipping duplicate method: " + methodDeclaration.GetText().ToString());
                return null;
            }

            seenMethodSignatures.Add(simpleMethodSignature);

            // Add the rest of the modifiers here that would mangle the signature check
            if (baseTypes.Count > 0 && methodExtension != null && methodExtension.Override != null && methodExtension.Override == true) {
                var matchingParentMethods = methodExtension.MatchingParentMethods;

                // Go through the parents, check if the parents are allowed to be generated
                // and add the new keyword if the matching method is found in one allowed to be generated
                // TODO: We can get rid of this once we start properly generating generic classes.
                // Since we just ignore any class that has '<' in it.
                foreach (var matchingMethod in matchingParentMethods) {
                    var parent = matchingMethod.DeclaringType;
                    if (!REFrameworkNET.AssemblyGenerator.validTypes.Contains(parent.FullName)) {
                        continue;
                    }

                    methodDeclaration = methodDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword));
                    break;
                }
            }

            return methodDeclaration;
        }).Where(method => method != null).Select(method => method!);

        if (matchingMethods == null) {
            return typeDeclaration;
        }
        
        return typeDeclaration.AddMembers(matchingMethods.ToArray());
    }

    private TypeDeclarationSyntax GenerateValueTypeMethods() {
        if (typeDeclaration == null) {
            throw new Exception("Type declaration is null"); // This should never happen
        }

        if (methods.Count == 0) {
            return typeDeclaration!;
        }

        HashSet<string> seenMethodSignatures = [];

        var validMethods = GetValidMethods();

        var matchingMethods = validMethods.Select(method =>
        {
            var returnType = MakeProperType(method.ReturnType, t);

            //string simpleMethodSignature = returnType.GetText().ToString();
            string simpleMethodSignature = ""; // Return types are not part of the signature. Return types are not overloaded.
            
            var methodName = new string(method.Name);
            var methodExtension = Il2CppDump.GetMethodExtension(method);

            // Hacky fix for MHR because parent classes have the same method names
            // while we support that, we don't support constructed generic arguments yet, they are just "object"
            if (methodName == "sortCountList") {
                Console.WriteLine("Skipping sortCountList");
                return null;
            }

            var methodDeclaration = SyntaxFactory.MethodDeclaration(returnType, methodName ?? "UnknownMethod")
                .AddModifiers(new SyntaxToken[]{SyntaxFactory.Token(SyntaxKind.PublicKeyword)})
                /*.AddBodyStatements(SyntaxFactory.ParseStatement("throw new System.NotImplementedException();"))*/;

            if (valueTypeMethods.Contains(methodName)) {
                methodDeclaration = methodDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword));
            }

            if (operatorTokens.ContainsKey(methodName ?? "UnknownMethod")) {
                // Add SpecialName attribute to the method
                methodDeclaration = methodDeclaration.AddAttributeLists(
                    SyntaxFactory.AttributeList().AddAttributes(SyntaxFactory.Attribute(
                        SyntaxFactory.ParseName("global::System.Runtime.CompilerServices.SpecialName"))
                    )
                );
            }

            simpleMethodSignature += methodName;

            // Create a private static field that holds the REFrameworkNET.Method
            var internalFieldName = "INTERNAL_" + method.Name + method.GetIndex().ToString();
            var methodVariableDeclaration = SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("global::REFrameworkNET.Method"))
                .AddVariables(SyntaxFactory.VariableDeclarator(internalFieldName).WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression("REFType.GetMethod(\"" + method.GetMethodSignature() + "\")"))));

            var methodFieldDeclaration = SyntaxFactory.FieldDeclaration(methodVariableDeclaration).AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword), 
                SyntaxFactory.Token(SyntaxKind.StaticKeyword), 
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
            internalFieldDeclarations.Add(methodFieldDeclaration);

            // Add full method name as a MethodName attribute to the method
            methodDeclaration = methodDeclaration.AddAttributeLists(
                SyntaxFactory.AttributeList().AddAttributes(SyntaxFactory.Attribute(
                    SyntaxFactory.ParseName("global::REFrameworkNET.Attributes.Method"),
                    SyntaxFactory.ParseAttributeArgumentList("(" + method.GetIndex().ToString() + ", global::REFrameworkNET.FieldFacadeType.None)")))
                );

            var anyOutParams = false;
            List<string> paramNames = [];

            var runtimeMethod = method.GetRuntimeMethod();
            if (runtimeMethod == null)
            {
                REFrameworkNET.API.LogWarning("Method " + method.DeclaringType.FullName + "." + method.Name + " has a null runtime method");
                return null;
            }

            var runtimeParams = runtimeMethod.Call("GetParameters") as REFrameworkNET.ManagedObject;

            if (method.Parameters.Count > 0) {
                // If any of the params have ! in them, skip this method
                if (method.Parameters.Any(param => param != null && (param.Type == null || (param.Type != null && param.Type.FullName.Contains('!'))))) {
                    return null;
                }

                List<ParameterSyntax> parameters = [];

                if (runtimeParams != null) {
                    var methodActualRetval = method.GetReturnType();
                    var unknownArgCount = 0u;

                    foreach (dynamic param in runtimeParams) {
                        /*if (param.get_IsRetval() == true) {
                            continue;
                        }*/

                        var paramDef = (REFrameworkNET.TypeDefinition)param.GetTypeDefinition();
                        var paramName = param.get_Name();

                        if (paramName == null || paramName == "") {
                            //paramName = "UnknownParam";
                            paramName = "arg" + unknownArgCount.ToString();
                            ++unknownArgCount;
                        }

                        if (paramName == "object") {
                            paramName = "object_"; // object is a reserved keyword.
                        }

                        var paramType = param.get_ParameterType();

                        if (paramType == null) {
                            paramNames.Add(paramName);
                            parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(SyntaxFactory.ParseTypeName("object")));
                            continue;
                        }

                        var parsedParamName = new string(paramName as string);

                        var isByRef = paramType.IsByRefImpl();
                        var isPointer = paramType.IsPointerImpl();
                        var isOut = paramDef != null && paramDef.FindMethod("get_IsOut") != null ? param.get_IsOut() : false;
                        var paramTypeDef = (REFrameworkNET.TypeDefinition)paramType.get_TypeHandle();

                        var paramTypeSyntax = MakeProperType(paramTypeDef, t);
                        
                        System.Collections.Generic.List<SyntaxToken> modifiers = [];

                        if (isOut == true) {
                            simpleMethodSignature += "out";
                            modifiers.Add(SyntaxFactory.Token(SyntaxKind.OutKeyword));
                            anyOutParams = true;
                        }

                        if (isByRef == true) {
                            // can only be either ref or out.
                            if (!isOut) {
                                simpleMethodSignature += "ref " + paramTypeSyntax.GetText().ToString();
                                modifiers.Add(SyntaxFactory.Token(SyntaxKind.RefKeyword));
                            }

                            parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(SyntaxFactory.ParseTypeName(paramTypeSyntax.ToString())).AddModifiers(modifiers.ToArray()));
                        } else if (isPointer == true) {
                            simpleMethodSignature += "ptr " + paramTypeSyntax.GetText().ToString();
                            parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(SyntaxFactory.ParseTypeName(paramTypeSyntax.ToString() + "*")).AddModifiers(modifiers.ToArray()));

                            parsedParamName = "(global::System.IntPtr) " + parsedParamName;
                        } else {
                            simpleMethodSignature += paramTypeSyntax.GetText().ToString();
                            parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(paramTypeSyntax).AddModifiers(modifiers.ToArray()));
                        }

                        paramNames.Add(parsedParamName);
                    }

                    methodDeclaration = methodDeclaration.AddParameterListParameters([.. parameters]);
                }
            } else {
                simpleMethodSignature += "()";
            }

            if (seenMethodSignatures.Contains(simpleMethodSignature)) {
                Console.WriteLine("Skipping duplicate method: " + methodDeclaration.GetText().ToString());
                return null;
            }

            // We'll be working with pointers, so we need to mark the method as unsafe
            methodDeclaration = methodDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));

            var isVoidReturn = method.ReturnType.FullName == "System.Void";

            // Generate method body
            // The approach here is to inspect how each parameter is to be passed to the method.
            // - Reference types are currently still proxies, so we need to obtain the actual object reference
            // - Value types with size <= sizeof(void*) are passed by value (unless they are ref/in/out)
            // - Value types with size > sizeof(void*) are passed by reference
            // - Pointers are passed as-is
            List<StatementSyntax> bodyStatements = [];
            
            if (!isVoidReturn) {
                bodyStatements.Add(SyntaxFactory.ParseStatement("global::REFrameworkNET.InvokeRet __result;"));
            }

            var argList = "";

            if (method.Parameters.Count > 0) {
                bodyStatements.Add(SyntaxFactory.ParseStatement(
                    $"global::System.Span<ulong> __args = stackalloc ulong[{method.Parameters.Count}];"));

                for (var i = 0; i < method.Parameters.Count; i++) {
                    var param = method.Parameters[i];
                    if (param == null) {
                        continue;
                    }

                    var paramType = param.Type;
                    if (paramType == null) {
                        continue;
                    }

                    var argName = paramNames[i];

                    if (paramType.IsValueType()) {
                        if (paramType.IsPrimitive()) {
                            switch (paramType.FullName) {
                                case "System.Single":
                                    // floats are passed as doubles
                                    bodyStatements.Add(SyntaxFactory.ParseStatement($"var {argName}__conv = (double) {argName};"));
                                    argName += "__conv";
                                    break;
                                case "System.SByte" or "System.Int16" or "System.Int32":
                                    bodyStatements.Add(SyntaxFactory.ParseStatement($"var {argName}__conv = (long) {argName};"));
                                    argName += "__conv";
                                    break;
                                case "System.Byte" or "System.UInt16" or "System.UInt32":
                                    bodyStatements.Add(SyntaxFactory.ParseStatement($"var {argName}__conv = (ulong) {argName};"));
                                    argName += "__conv";
                                    break;
                            }

                            bodyStatements.Add(SyntaxFactory.ParseStatement($"__args[{i}] = (ulong) Unsafe.As<ulong>(ref {argName});"));
                        } else {
                            // Non-primitive value types are passed by reference
                            bodyStatements.Add(SyntaxFactory.ParseStatement($"__args[{i}] = (ulong) Unsafe.AsPointer(ref {argName});"));
                        }
                    } else {
                        bodyStatements.Add(paramType.FullName == "System.String"
                            ? SyntaxFactory.ParseStatement($"var {argName}__conv = VM.CreateString({argName});")
                            : SyntaxFactory.ParseStatement($"var {argName}__conv = (REFrameworkNET.IObject) {argName};"));

                        bodyStatements.Add(SyntaxFactory.ParseStatement($"__args[{i}] = (ulong) {argName}__conv.Ptr();"));
                    }
                }
                
                bodyStatements.Add(SyntaxFactory.ParseStatement($"fixed (ulong* __args_ptr = __args)"));
                argList = "(ulong) __args_ptr, __args.Length";
            } else {
                argList = "0, 0";
            }

            bodyStatements.Add(SyntaxFactory.ParseStatement($"fixed ({actualName}* __pthis = &this)"));
            
            if (isVoidReturn) {
                bodyStatements.Add(SyntaxFactory.ParseStatement($"{internalFieldName}.InvokeRaw(__pthis, {argList});"));
            } else {
                bodyStatements.Add(SyntaxFactory.ParseStatement($"__result = {internalFieldName}.InvokeRaw(__pthis, {argList});"));
            }
            
            if (!isVoidReturn) {
                GenerateReturnValueHandler(bodyStatements, method.ReturnType, returnType, internalFieldName);
            }

            return methodDeclaration.AddBodyStatements(bodyStatements.ToArray());
        });

        if (matchingMethods == null) {
            return typeDeclaration;
        }

        return typeDeclaration.AddMembers(matchingMethods.ToArray());
    }

    private void GenerateReturnValueHandler(List<StatementSyntax> statements, TypeDefinition returnType, 
        TypeSyntax actualReturnType, string methodFieldName) {
        if (returnType.IsPrimitive()) {
            switch (returnType.FullName) {
                case "System.Single":
                    statements.Add(SyntaxFactory.ParseStatement("return (float) __result.Double;"));
                    break;
                case "System.Double":
                    statements.Add(SyntaxFactory.ParseStatement("return __result.Double;"));
                    break;
                case "System.SByte":
                    statements.Add(SyntaxFactory.ParseStatement("return __result.SByte;"));
                    break;
                case "System.Int16":
                    statements.Add(SyntaxFactory.ParseStatement("return __result.Int16;"));
                    break;
                case "System.Int32":
                    statements.Add(SyntaxFactory.ParseStatement("return __result.Int32;"));
                    break;
                case "System.Int64":
                    statements.Add(SyntaxFactory.ParseStatement("return __result.Int64;"));
                    break;
                case "System.Byte":
                    statements.Add(SyntaxFactory.ParseStatement("return __result.Byte;"));
                    break;
                case "System.UInt16":
                    statements.Add(SyntaxFactory.ParseStatement("return __result.Word;"));
                    break;
                case "System.UInt32":
                    statements.Add(SyntaxFactory.ParseStatement("return __result.DWord;"));
                    break;
                case "System.UInt64":
                    statements.Add(SyntaxFactory.ParseStatement("return __result.QWord;"));
                    break;
                case "System.Boolean":
                    statements.Add(SyntaxFactory.ParseStatement("return __result.Byte != 0;"));
                    break;
                default:
                    throw new NotImplementedException("Unsupported primitive type: " + returnType.FullName);
            }
        } else if (t.IsValueType() || t.IsEnum()) {
            statements.Add(
                SyntaxFactory.ParseStatement(
                    $"return Unsafe.As<global::REFrameworkNET.InvokeRet, {actualReturnType}>(ref __result);"));
        } else {
            var conversionCall = $"Utility.ConvertReferenceTypeResult(ref __result, {methodFieldName}.ReturnType)";
            switch (t.GetVMObjType())
            {
                case VMObjType.Object:
                case VMObjType.Array:
                    statements.Add(SyntaxFactory.ParseStatement(
                        $"return ((global::REFrameworkNET.ManagedObject) {conversionCall}).As<{actualReturnType}>();"));
                    break;
                case VMObjType.String:
                    statements.Add(SyntaxFactory.ParseStatement($"return (string) {conversionCall};"));
                    break;
                default:
                    throw new NotImplementedException();
            }
        }
    }

    private TypeDeclarationSyntax? GenerateNestedTypes() {
        if (this.typeDeclaration == null) {
            return null;
        }

        HashSet<REFrameworkNET.TypeDefinition>? nestedTypes = Il2CppDump.GetTypeExtension(t)?.NestedTypes;

        foreach (var nestedT in nestedTypes ?? []) {
            var nestedTypeName = nestedT.FullName ?? "";

            //System.Console.WriteLine("Nested type: " + nestedTypeName);

            if (nestedTypeName == "") {
                continue;
            }

            if (nestedTypeName.Contains("[") || nestedTypeName.Contains("]") || nestedTypeName.Contains('<')) {
                continue;
            }

            if (nestedTypeName.Split('.').Last() == "file") {
                nestedTypeName = nestedTypeName.Replace("file", "@file");
            }

            // Enum
            if (nestedT.IsEnum()) {
                var nestedEnumGenerator = new EnumGenerator(nestedTypeName.Split('.').Last(), nestedT);

                AssemblyGenerator.ForEachArrayType(nestedT, (arrayType) => {
                    var arrayTypeName = AssemblyGenerator.typeRenames[arrayType];

                    var arrayClassGenerator = new ClassGenerator(
                        arrayTypeName,
                        arrayType
                    );

                    if (arrayClassGenerator.TypeDeclaration != null) {
                        this.Update(this.typeDeclaration.AddMembers(arrayClassGenerator.TypeDeclaration));
                    }
                });

                if (nestedEnumGenerator.EnumDeclaration != null) {
                    this.Update(this.typeDeclaration.AddMembers(nestedEnumGenerator.EnumDeclaration));
                }

                continue;
            }

            var nestedGenerator = new ClassGenerator(
                nestedTypeName.Split('.').Last(),
                nestedT
            );

            if (nestedGenerator.TypeDeclaration == null) {
                continue;
            }

            AssemblyGenerator.ForEachArrayType(nestedT, (arrayType) => {
                var arrayTypeName = AssemblyGenerator.typeRenames[arrayType];

                var arrayClassGenerator = new ClassGenerator(
                    arrayTypeName,
                    arrayType
                );

                if (arrayClassGenerator.TypeDeclaration != null) {
                    //this.Update(this.typeDeclaration.AddMembers(arrayClassGenerator.TypeDeclaration));
                    this.Update(this.typeDeclaration.AddMembers(arrayClassGenerator.TypeDeclaration));
                }
            });

            if (nestedGenerator.TypeDeclaration != null) {
                this.Update(this.typeDeclaration.AddMembers(nestedGenerator.TypeDeclaration));
            }
        }

        return typeDeclaration;
    }
}