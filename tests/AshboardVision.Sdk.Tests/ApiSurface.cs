using System.Globalization;
using System.Reflection;

namespace AshboardVision.Sdk.Tests;

/// <summary>
/// Décrit, ligne par ligne, tout ce qui est public ou protégé dans des assemblages : types, hiérarchie, constructeurs, méthodes,
/// propriétés, événements, champs. C'est la base de l'instantané <c>api-1.0.txt</c>.
/// <para>
/// Une ligne = un fait indépendant, <c>[assemblage] Type :: fait</c>, pour qu'on puisse dire « il manque telle ligne » (un retrait ou
/// un changement de signature) sans que les ajouts gênent. Le résultat est trié en ordre ordinal et ne contient aucun numéro de version
/// d'assemblage, de sorte qu'il est identique d'une machine à l'autre et d'une construction à l'autre.
/// </para>
/// </summary>
internal static class ApiSurface
{
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void", [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(int)] = "int", [typeof(uint)] = "uint",
        [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(float)] = "float", [typeof(double)] = "double",
        [typeof(decimal)] = "decimal", [typeof(char)] = "char", [typeof(string)] = "string", [typeof(object)] = "object",
    };

    public static IReadOnlyList<string> Describe(params Assembly[] assemblies)
    {
        var lines = new List<string>();
        foreach (var assembly in assemblies)
        {
            var prefix = "[" + assembly.GetName().Name + "] ";
            foreach (var type in assembly.GetExportedTypes())
            {
                var owner = prefix + TypeName(type);
                lines.Add(owner + " :: " + TypeHeader(type));
                foreach (var line in TypeFacts(type))
                {
                    lines.Add(owner + " :: " + line);
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static string TypeHeader(Type type)
    {
        var kind = type switch
        {
            { IsInterface: true } => "interface",
            { IsEnum: true } => "enum " + TypeName(Enum.GetUnderlyingType(type)),
            { IsValueType: true } => "struct",
            _ when typeof(Delegate).IsAssignableFrom(type) => "delegate",
            { IsAbstract: true, IsSealed: true } => "static class",
            { IsAbstract: true } => "abstract class",
            { IsSealed: true } => "sealed class",
            _ => "class",
        };
        return "type public " + kind;
    }

    private static IEnumerable<string> TypeFacts(Type type)
    {
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum)
            && baseType != typeof(MulticastDelegate))
        {
            yield return "extends " + TypeName(baseType);
        }

        foreach (var contract in type.GetInterfaces())
        {
            yield return "implements " + TypeName(contract);
        }

        var nullability = new NullabilityInfoContext();
        var accessors = new HashSet<MethodInfo>();

        foreach (var constructor in type.GetConstructors(AllDeclared).Where(c => Access(c) is not null))
        {
            yield return "constructor " + Access(constructor) + (constructor.IsStatic ? " static" : "") + " (" + Parameters(constructor.GetParameters(), nullability) + ")";
        }

        foreach (var property in type.GetProperties(AllDeclared))
        {
            var get = property.GetMethod;
            var set = property.SetMethod;
            if (get is not null) { accessors.Add(get); }
            if (set is not null) { accessors.Add(set); }

            var visible = new[] { (Name: "get", Method: get), (Name: "set", Method: set) }
                .Where(accessor => accessor.Method is not null && Access(accessor.Method) is not null)
                .ToArray();
            if (visible.Length == 0)
            {
                continue;
            }

            var main = visible[0].Method!;
            var accessorText = string.Join(" ", visible.Select(accessor =>
                Access(accessor.Method!) + " " + (accessor.Name == "set" && IsInitOnly(accessor.Method!) ? "init" : accessor.Name) + ";"));
            var indexes = property.GetIndexParameters();
            yield return "property " + Modifiers(main) + Format(property.PropertyType, nullability.Create(property)) + " " + property.Name
                + (indexes.Length > 0 ? "[" + Parameters(indexes, nullability) + "]" : "") + " { " + accessorText + " }";
        }

        foreach (var @event in type.GetEvents(AllDeclared))
        {
            var add = @event.AddMethod;
            if (add is not null) { accessors.Add(add); }
            if (@event.RemoveMethod is not null) { accessors.Add(@event.RemoveMethod); }
            if (add is not null && Access(add) is not null)
            {
                yield return "event " + Modifiers(add) + Format(@event.EventHandlerType!, nullability.Create(@event)) + " " + @event.Name;
            }
        }

        // Le « <Clone>$ » d'un record est un détail du compilateur (son nom n'est pas appelable en C#) : hors de l'instantané.
        foreach (var method in type.GetMethods(AllDeclared).Where(m => !accessors.Contains(m) && Access(m) is not null && m.Name[0] != '<'))
        {
            var generics = method.IsGenericMethodDefinition
                ? "<" + string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name)) + ">"
                : "";
            yield return "method " + Modifiers(method) + Format(method.ReturnType, nullability.Create(method.ReturnParameter)) + " "
                + method.Name + generics + "(" + Parameters(method.GetParameters(), nullability) + ")";
        }

        foreach (var field in type.GetFields(AllDeclared).Where(f => Access(f) is not null && !f.IsSpecialName))
        {
            var kind = field.IsLiteral ? "const " : (field.IsStatic ? "static " : "") + (field.IsInitOnly ? "readonly " : "");
            var value = field.IsLiteral ? " = " + Literal(field.GetRawConstantValue()) : "";
            yield return "field " + Access(field) + " " + kind + Format(field.FieldType, nullability.Create(field)) + " " + field.Name + value;
        }
    }

    private static string? Access(MethodBase method) =>
        method.IsPublic ? "public" : method.IsFamilyOrAssembly ? "protected internal" : method.IsFamily ? "protected" : null;

    private static string? Access(FieldInfo field) =>
        field.IsPublic ? "public" : field.IsFamilyOrAssembly ? "protected internal" : field.IsFamily ? "protected" : null;

    // « public », « protected » et les modificateurs utiles à qui hérite : static, abstract, virtual, override, sealed.
    private static string Modifiers(MethodInfo method)
    {
        var text = Access(method) + " ";
        if (method.IsStatic)
        {
            return text + "static ";
        }

        if (method.IsAbstract)
        {
            return text + "abstract ";
        }

        if (method.GetBaseDefinition().DeclaringType != method.DeclaringType)
        {
            return text + (method.IsFinal ? "sealed override " : "override ");
        }

        // Une méthode « virtual final » qui n'en redéfinit aucune est une implémentation d'interface : pour un appelant, elle n'est pas virtuelle.
        return method.IsVirtual && !method.IsFinal ? text + "virtual " : text;
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    private static string Parameters(ParameterInfo[] parameters, NullabilityInfoContext nullability) =>
        string.Join(", ", parameters.Select(parameter =>
        {
            var type = parameter.ParameterType;
            var prefix = parameter.IsOut ? "out " : parameter.IsIn ? "in " : type.IsByRef ? "ref " : "";
            var text = prefix + Format(type.IsByRef ? type.GetElementType()! : type, nullability.Create(parameter)) + " " + parameter.Name;
            return parameter.HasDefaultValue ? text + " = " + Literal(parameter.RawDefaultValue) : text;
        }));

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };

    private static string TypeName(Type type) => Format(type, null);

    // Le nom lisible d'un type, avec « ? » pour un type référence annoté nullable (changer string en string? se voit dans l'instantané).
    private static string Format(Type type, NullabilityInfo? nullability)
    {
        if (type.IsByRef)
        {
            return Format(type.GetElementType()!, nullability);
        }

        if (type.IsArray)
        {
            return Format(type.GetElementType()!, nullability?.ElementType) + "[" + new string(',', type.GetArrayRank() - 1) + "]" + Mark(type, nullability);
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Format(underlying, null) + "?";
        }

        if (type.IsGenericParameter)
        {
            return type.Name + Mark(type, nullability);
        }

        if (Keywords.TryGetValue(type, out var keyword))
        {
            return keyword + Mark(type, nullability);
        }

        var name = type.IsNested ? Format(type.DeclaringType!, null) + "." + type.Name : type.Namespace + "." + type.Name;
        if (type.IsGenericType)
        {
            var arguments = type.GetGenericArguments();
            var tick = name.IndexOf('`');
            var shown = arguments.Select((argument, index) =>
                Format(argument, nullability is not null && index < nullability.GenericTypeArguments.Length ? nullability.GenericTypeArguments[index] : null));
            name = name[..tick] + "<" + string.Join(", ", shown) + ">";
        }

        return name + Mark(type, nullability);
    }

    private static string Mark(Type type, NullabilityInfo? nullability) =>
        !type.IsValueType && nullability is not null
        && (nullability.ReadState == NullabilityState.Nullable || nullability.WriteState == NullabilityState.Nullable)
            ? "?"
            : "";
}
