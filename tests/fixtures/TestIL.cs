namespace TestIL
{
    public static class Simple
    {
        public static int AddOne(int x) => x + 1;

        public static int Add(int a, int b) => a + b;
        public static int Add(int a, int b, int c) => a + b + c;

        public static string Greet(string name) => "Hello, " + name;

        public static int Branch(int x)
        {
            if (x > 0)
                return 1;
            return -1;
        }

        static int counter;
        public static int Inc()
        {
            counter = counter + 1;
            return counter;
        }

        public static object[] MakeArray() => new string[3];
    }
}
