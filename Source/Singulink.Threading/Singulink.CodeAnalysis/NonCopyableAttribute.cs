namespace Singulink.CodeAnalysis;

[AttributeUsage(AttributeTargets.Struct | AttributeTargets.GenericParameter, Inherited = false)]
internal sealed class NonCopyableAttribute : Attribute
{
}
