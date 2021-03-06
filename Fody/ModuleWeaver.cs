﻿using System;
using System.Collections;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Xml.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;
using System.Globalization;
using System.CodeDom.Compiler;
using System.Diagnostics;
using ToString.Fody.Extensions;

public class ModuleWeaver
{
    public ModuleDefinition ModuleDefinition { get; set; }
    public IAssemblyResolver AssemblyResolver { get; set; }
    public XElement Config { get; set; }

    TypeReference stringBuilderType;

    MethodReference appendString;
    MethodReference moveNext;
    MethodReference current;
    MethodReference getEnumerator;
    MethodReference getInvariantCulture;
    MethodReference formatMethod;

    public IEnumerable<TypeDefinition> GetMachingTypes()
    {
        return ModuleDefinition.GetTypes().Where(x => x.CustomAttributes.Any(a => a.AttributeType.Name == "ToStringAttribute"));
    }

    public void Execute()
    {
        stringBuilderType = ModuleDefinition.ImportReference(typeof (StringBuilder));
        appendString = ModuleDefinition.ImportReference(typeof(StringBuilder).GetMethod("Append", new[] { typeof(object) }));
        moveNext = ModuleDefinition.ImportReference(typeof(IEnumerator).GetMethod("MoveNext"));
        current = ModuleDefinition.ImportReference(typeof(IEnumerator).GetProperty("Current").GetGetMethod());
        getEnumerator = ModuleDefinition.ImportReference(typeof(IEnumerable).GetMethod("GetEnumerator"));
        formatMethod = ModuleDefinition.ImportReference(ModuleDefinition.TypeSystem.String.Resolve().FindMethod("Format", "IFormatProvider", "String", "Object[]"));

        var cultureInfoType = ModuleDefinition.ImportReference(typeof(CultureInfo)).Resolve();
        var invariantCulture = cultureInfoType.Properties.Single(x => x.Name == "InvariantCulture");
        getInvariantCulture = ModuleDefinition.ImportReference(invariantCulture.GetMethod);

        foreach (var type in GetMachingTypes())
        {
            AddToString(type);
        }

        RemoveReference();
    }


    void AddToString(TypeDefinition type)
    {
        var methodAttributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual;
        var strType = ModuleDefinition.TypeSystem.String;
        var method = new MethodDefinition("ToString", methodAttributes, strType);
        method.Body.Variables.Add(new VariableDefinition(new ArrayType(ModuleDefinition.TypeSystem.Object)));
        var allProperties = type.GetProperties().Where(x => !x.HasParameters).ToArray();
        var properties = RemoveIgnoredProperties(allProperties);

        var format = GetFormatString(type, properties);

        var body = method.Body;
        body.InitLocals = true;
        var ins = body.Instructions;

        var hasCollections = properties.Any(x => !x.PropertyType.IsGenericParameter && x.PropertyType.Resolve().IsCollection());
        if (hasCollections)
        {
            method.Body.Variables.Add(new VariableDefinition(stringBuilderType));

            var enumeratorType = ModuleDefinition.ImportReference(typeof (IEnumerator));
            method.Body.Variables.Add(new VariableDefinition(enumeratorType));

            method.Body.Variables.Add(new VariableDefinition(ModuleDefinition.TypeSystem.Boolean));

            method.Body.Variables.Add(new VariableDefinition(new ArrayType(ModuleDefinition.TypeSystem.Object)));
        }

        var genericOffset = !type.HasGenericParameters ? 0 : type.GenericParameters.Count;
        AddInitCode(ins, format, properties, genericOffset);

        if (type.HasGenericParameters)
        {
            AddGenericParameterNames(type, ins);
        }

        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];
            AddPropertyCode(method.Body, i + genericOffset, property, type, method.Body.Variables);
        }

        AddMethodAttributes(method);

        AddEndCode(body);
        body.OptimizeMacros();

        var toRemove = type.Methods.FirstOrDefault(x => x.Name == method.Name && x.Parameters.Count == 0);
        if (toRemove != null)
        {
            type.Methods.Remove(toRemove);
        }

        type.Methods.Add(method);

        RemoveFodyAttributes(type, allProperties);
    }

    void AddGenericParameterNames(TypeDefinition type, Collection<Instruction> ins)
    {
        var typeType = ModuleDefinition.ImportReference(typeof(Type)).Resolve();
        var memberInfoType = ModuleDefinition.ImportReference(typeof(System.Reflection.MemberInfo)).Resolve();
        var getTypeMethod = ModuleDefinition.ImportReference(ModuleDefinition.TypeSystem.Object.Resolve().FindMethod("GetType"));
        var getGenericArgumentsMethod = ModuleDefinition.ImportReference(typeType.FindMethod("GetGenericArguments"));
        var nameProperty = memberInfoType.Properties.Single(x => x.Name == "Name");
        var nameGet = ModuleDefinition.ImportReference(nameProperty.GetMethod);

        for (var i = 0; i < type.GenericParameters.Count; i++)
        {
            ins.Add(Instruction.Create(OpCodes.Ldloc_0));
            ins.Add(Instruction.Create(OpCodes.Ldc_I4, i));

            ins.Add(Instruction.Create(OpCodes.Ldarg_0));
            ins.Add(Instruction.Create(OpCodes.Callvirt, getTypeMethod));
            ins.Add(Instruction.Create(OpCodes.Callvirt, getGenericArgumentsMethod));
            ins.Add(Instruction.Create(OpCodes.Ldc_I4, i));
            ins.Add(Instruction.Create(OpCodes.Ldelem_Ref));
            ins.Add(Instruction.Create(OpCodes.Callvirt, nameGet));

            ins.Add(Instruction.Create(OpCodes.Stelem_Ref));
        }
    }

    void AddMethodAttributes(MethodDefinition method)
    {
        var generatedConstructor = ModuleDefinition.ImportReference(typeof(GeneratedCodeAttribute).GetConstructor(new[] { typeof(string), typeof(string) }));

        var version = typeof(ModuleWeaver).Assembly.GetName().Version.ToString();

        var generatedAttribute = new CustomAttribute(generatedConstructor);
        generatedAttribute.ConstructorArguments.Add(new CustomAttributeArgument(ModuleDefinition.TypeSystem.String, "Fody.ToString"));
        generatedAttribute.ConstructorArguments.Add(new CustomAttributeArgument(ModuleDefinition.TypeSystem.String, version));
        method.CustomAttributes.Add(generatedAttribute);

        var debuggerConstructor = ModuleDefinition.ImportReference(typeof(DebuggerNonUserCodeAttribute).GetConstructor(Type.EmptyTypes));
        var debuggerAttribute = new CustomAttribute(debuggerConstructor);
        method.CustomAttributes.Add(debuggerAttribute);
    }

    void AddEndCode(MethodBody body)
    {
        var stringType = ModuleDefinition.TypeSystem.String.Resolve();
        var formatMethod = ModuleDefinition.ImportReference(stringType.FindMethod("Format", "IFormatProvider", "String", "Object[]"));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc_0));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, formatMethod));
        body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }

    void AddInitCode(Collection<Instruction> ins, string format, PropertyDefinition[] properties, int genericOffset)
    {
        var cultureInfoType = ModuleDefinition.ImportReference(typeof(CultureInfo)).Resolve();
        var invariantCulture = cultureInfoType.Properties.Single(x => x.Name == "InvariantCulture");
        var getInvariantCulture = ModuleDefinition.ImportReference(invariantCulture.GetMethod);
        ins.Add(Instruction.Create(OpCodes.Call, getInvariantCulture));
        ins.Add(Instruction.Create(OpCodes.Ldstr, format));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4, properties.Length + genericOffset));
        ins.Add(Instruction.Create(OpCodes.Newarr, ModuleDefinition.TypeSystem.Object));
        ins.Add(Instruction.Create(OpCodes.Stloc_0));
    }

    void AddPropertyCode(MethodBody body, int index, PropertyDefinition property, TypeDefinition targetType, Collection<VariableDefinition> variables)
    {
        var ins = body.Instructions;

        ins.Add(Instruction.Create(OpCodes.Ldloc_0));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4, index));

        var get = ModuleDefinition.ImportReference(property.GetGetMethod(targetType));
            
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Call, get));

        if ( get.ReturnType.IsValueType)
        {
            var returnType = ModuleDefinition.ImportReference(property.GetMethod.ReturnType);
            if( returnType.FullName == "System.DateTime" )
            {
                var convertToUtc = ModuleDefinition.ImportReference(returnType.Resolve().FindMethod( "ToUniversalTime" ));
                
                var variable = new VariableDefinition(returnType);
                variables.Add(variable);
                ins.Add(Instruction.Create(OpCodes.Stloc, variable));
                ins.Add(Instruction.Create(OpCodes.Ldloca, variable));
                ins.Add(Instruction.Create(OpCodes.Call, convertToUtc));
            }
            ins.Add(Instruction.Create(OpCodes.Box, returnType));
        }
        else
        {
            var propType = property.PropertyType.Resolve();
            var isCollection = !property.PropertyType.IsGenericParameter && propType.IsCollection();

            if (isCollection)
            {
                AssignFalseToFirstFLag(ins);

                If(ins, 
                    nc => nc.Add(Instruction.Create(OpCodes.Dup)),
                    nt =>
                    {
                        GetEnumerator(nt);

                        NewStringBuilder(nt);

                        AppendString(nt, ListStart);

                        While(nt,
                            c =>
                            {
                                c.Add(Instruction.Create(OpCodes.Ldloc_2));
                                c.Add(Instruction.Create(OpCodes.Callvirt, moveNext));
                            },
                            b =>
                            {
                                AppendSeparator(b);

                                ins.Add(Instruction.Create(OpCodes.Ldloc_1));
                                If(ins,
                                    c =>
                                    {
                                        c.Add(Instruction.Create(OpCodes.Ldloc_2));
                                        c.Add(Instruction.Create(OpCodes.Callvirt, current));
                                    },
                                    t =>
                                    {
                                        t.Add(Instruction.Create(OpCodes.Call, getInvariantCulture));

                                        string format;
                                        var collectionType = ((GenericInstanceType)property.PropertyType).GenericArguments[0];
                                        if (HaveToAddQuotes(collectionType))
                                        {
                                            format = "\"{0}\"";
                                        }
                                        else
                                        {
                                            format = "{0}";
                                        }

                                        t.Add(Instruction.Create(OpCodes.Ldstr, format));  

                                        t.Add(Instruction.Create(OpCodes.Ldc_I4, 1));
                                        t.Add(Instruction.Create(OpCodes.Newarr, ModuleDefinition.TypeSystem.Object)); 
                                        t.Add(Instruction.Create(OpCodes.Stloc, body.Variables[4])); 
                                        t.Add(Instruction.Create(OpCodes.Ldloc, body.Variables[4])); 

                                        t.Add(Instruction.Create(OpCodes.Ldc_I4_0)); 

                                        t.Add(Instruction.Create(OpCodes.Ldloc_2)); 
                                        t.Add(Instruction.Create(OpCodes.Callvirt, current)); 


                                        t.Add(Instruction.Create(OpCodes.Stelem_Ref));
                                        t.Add(Instruction.Create(OpCodes.Ldloc, body.Variables[4])); 

                                        t.Add(Instruction.Create(OpCodes.Call, formatMethod)); 
                                    },
                                    e => e.Add(Instruction.Create(OpCodes.Ldstr, "null")));
                                ins.Add(Instruction.Create(OpCodes.Callvirt, appendString));
                                ins.Add(Instruction.Create(OpCodes.Pop));
                            });

                        AppendString(ins, ListEnd);
                        StringBuilderToString(ins);       
                    },
                    nf =>
                    {
                        ins.Add(Instruction.Create(OpCodes.Pop));
                        ins.Add(Instruction.Create(OpCodes.Ldstr, "null")); 
                    });              
            }
            else
            {
                If(ins, 
                    c =>
                    {
                        ins.Add(Instruction.Create(OpCodes.Dup));
                        AddBoxing(property, targetType, c);
                    },
                    t => AddBoxing(property, targetType, t),
                    e =>
                    {
                        ins.Add(Instruction.Create(OpCodes.Pop));
                        ins.Add(Instruction.Create(OpCodes.Ldstr, "null"));   
                    });
            }
        }

        ins.Add(Instruction.Create(OpCodes.Stelem_Ref));
    }

    static void AddBoxing(PropertyDefinition property, TypeDefinition targetType, Collection<Instruction> ins)
    {
        if (property.PropertyType.IsValueType || property.PropertyType.IsGenericParameter)
        {
            var genericType = property.PropertyType.GetGenericInstanceType(targetType);
            ins.Add(Instruction.Create(OpCodes.Box, genericType));
        }
    }

    void NewStringBuilder(Collection<Instruction> ins)
    {
        var stringBuilderConstructor = ModuleDefinition.ImportReference(typeof (StringBuilder).GetConstructor(new Type[] {}));
        ins.Add(Instruction.Create(OpCodes.Newobj, stringBuilderConstructor));
        ins.Add(Instruction.Create(OpCodes.Stloc_1));
    }

    void GetEnumerator(Collection<Instruction> ins)
    {
        ins.Add(Instruction.Create(OpCodes.Callvirt, getEnumerator));
        ins.Add(Instruction.Create(OpCodes.Stloc_2));
    }

    static void AssignFalseToFirstFLag(Collection<Instruction> ins)
    {
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        ins.Add(Instruction.Create(OpCodes.Stloc_3));
    }

    void While(
        Collection<Instruction> ins,
        Action<Collection<Instruction>> condition,
        Action<Collection<Instruction>> body )
    {
        var loopBegin = Instruction.Create(OpCodes.Nop);
        var loopEnd = Instruction.Create(OpCodes.Nop);

        ins.Add(loopBegin);

        condition(ins);

        ins.Add(Instruction.Create(OpCodes.Brfalse, loopEnd));

        body(ins);

        ins.Add(Instruction.Create(OpCodes.Br, loopBegin));
        ins.Add(loopEnd);
    }

    void AppendString(Collection<Instruction> ins, string str)
    {
        ins.Add(Instruction.Create(OpCodes.Ldloc_1));
        ins.Add(Instruction.Create(OpCodes.Ldstr, str));
        ins.Add(Instruction.Create(OpCodes.Callvirt, appendString));
        ins.Add(Instruction.Create(OpCodes.Pop));
    }

    void StringBuilderToString(Collection<Instruction> ins)
    {
        ins.Add(Instruction.Create(OpCodes.Ldloc_1));
        var toStringMethod = ModuleDefinition.ImportReference(stringBuilderType.Resolve().FindMethod("ToString"));
        ins.Add(Instruction.Create(OpCodes.Callvirt, toStringMethod));
    }

    void If(Collection<Instruction> ins,
                    Action<Collection<Instruction>> condition,
                    Action<Collection<Instruction>> thenStatement,
                    Action<Collection<Instruction>> elseStatement)
    {
        var ifEnd = Instruction.Create(OpCodes.Nop);
        var ifElse = Instruction.Create(OpCodes.Nop);

        condition(ins);

        ins.Add(Instruction.Create(OpCodes.Brfalse, ifElse));

        thenStatement(ins);

        ins.Add(Instruction.Create(OpCodes.Br, ifEnd));
        ins.Add(ifElse);

        elseStatement(ins);

        ins.Add(ifEnd);
    }

    void AppendSeparator(Collection<Instruction> ins)
    {
        If(ins,
           c => c.Add(Instruction.Create(OpCodes.Ldloc_3)),
           t => AppendString(t, PropertiesSeparator),
           e =>
               {
                   ins.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                   ins.Add(Instruction.Create(OpCodes.Stloc_3));
               });
    }

    string GetFormatString(TypeDefinition type, PropertyDefinition[] properties)
    {
        var sb = new StringBuilder();
        var offset = 0;

        if (WrapWithBrackets)
        {
            sb.Append("{{");
        }

        if (WriteTypeName)
        {
            sb.AppendFormat("T{0}\"", PropertyNameToValueSeparator);

            if (!type.HasGenericParameters)
            {
                sb.Append(type.Name);
            }
            else
            {
                var name = type.Name.Remove(type.Name.IndexOf('`'));
                offset = type.GenericParameters.Count;
                sb.Append(name);
                sb.Append('<');
                for (var i = 0; i < offset; i++)
                {
                    sb.Append("{");
                    sb.Append(i);
                    sb.Append("}");
                    if (i + 1 != offset)
                    {
                        sb.Append(PropertiesSeparator);
                    }
                }
                sb.Append('>');
            }
            sb.Append("\"" + PropertiesSeparator);
        }

        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];
            sb.Append(property.Name);
            sb.Append(PropertyNameToValueSeparator);

            if (HaveToAddQuotes(property.PropertyType))
            {
                sb.Append('"');
            }

            sb.Append('{');
            sb.Append(i + offset);

            if (property.PropertyType.FullName == "System.DateTime")
            {
                sb.Append(":O");
            }
            if( property.PropertyType.FullName == "System.TimeSpan" )
            {
                sb.Append( ":c" );
            }

            sb.Append("}");

            if (HaveToAddQuotes(property.PropertyType))
            {
                sb.Append('"');
            }

            if (i != properties.Length - 1)
            {
                sb.Append(PropertiesSeparator);
            }
        }

        if (WrapWithBrackets)
        {
            sb.Append("}}");
        }

        var format = sb.ToString();
        return format;
    }

    static bool HaveToAddQuotes(TypeReference type)
    {
        var name = type.FullName;
        if(name == "System.String" || name == "System.Char" || name == "System.DateTime" || name == "System.TimeSpan"
            || name == "System.Guid")
        {
            return true;
        }

        var resolved = type.Resolve();
        return  resolved != null && resolved.IsEnum;
    }

    void RemoveReference()
    {
        var referenceToRemove = ModuleDefinition.AssemblyReferences.FirstOrDefault(x => x.Name == "ToString");
        if (referenceToRemove != null)
        {
            ModuleDefinition.AssemblyReferences.Remove(referenceToRemove);
        }
    }

    void RemoveFodyAttributes(TypeDefinition type, PropertyDefinition[] allProperties)
    {
        type.RemoveAttribute("ToStringAttribute");
        foreach (var property in allProperties)
        {
            property.RemoveAttribute("IgnoreDuringToStringAttribute");
        }
    }

    PropertyDefinition[] RemoveIgnoredProperties(PropertyDefinition[] allProperties)
    {
        return allProperties.Where(x => x.CustomAttributes.All(y => y.AttributeType.Name != "IgnoreDuringToStringAttribute")).ToArray();
    }

    private string PropertyNameToValueSeparator => ReadStringValueFromConfig("PropertyNameToValueSeparator", ": ");

    private string PropertiesSeparator => ReadStringValueFromConfig("PropertiesSeparator", ", ");

    private string ListStart => ReadStringValueFromConfig("ListStart", "[");

    private string ListEnd => ReadStringValueFromConfig("ListEnd", "]");

    private bool WrapWithBrackets => ReadBoolValueFromConfig("WrapWithBrackets", true);

    private bool WriteTypeName => ReadBoolValueFromConfig("WriteTypeName", true);

    private string ReadStringValueFromConfig(string nodeName, string defaultValue)
    {
        var node = Config?.Attributes().FirstOrDefault(a => a.Name.LocalName == nodeName);
        return node?.Value ?? defaultValue;
    }

    private bool ReadBoolValueFromConfig(string nodeName, bool defaultValue)
    {
        var node = Config?.Attributes().FirstOrDefault(a => a.Name.LocalName == nodeName);
        bool nodeValue;
        return node != null && bool.TryParse(node.Value, out nodeValue) 
            ? nodeValue 
            : defaultValue;
    }
}
