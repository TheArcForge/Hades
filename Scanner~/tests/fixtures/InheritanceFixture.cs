// Fixture for B2 supertype classification tests
namespace TestProject.Inheritance
{
    public interface IFoo {}
    public interface IBar<T> {}
    public class Base {}

    // P inherits class Base and two interfaces (IFoo, IBar<string>).
    // Correct: Base → inherits_from, IFoo → implements, IBar → implements
    public class P : Base, IFoo, IBar<string> {}

    // Q implements only an interface
    public class Q : IFoo {}

    // R is an interface itself
    public interface R : IFoo {}

    // S has a generic base type
    public class S : Base {}

    public struct MyStruct {}
    public enum MyEnum { A, B }
}
