using Foo = Some.Namespace.Bar;
using System.Collections.Generic;

namespace TestProject.B1
{
    public class B1Class
    {
        // alias rewrite: Foo -> Bar
        private Foo _x;

        // property type + generic arg
        public IList<Widget> Items { get; }

        // generic return type args
        public IFoo<Bar> Make()
        {
            return null;
        }

        // invocation generic args
        public void Run()
        {
            ServiceLocator.GetService<MyService>();
        }
    }
}
