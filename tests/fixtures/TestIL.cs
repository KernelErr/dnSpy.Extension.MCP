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

    // String-literal coverage for search_string_literals / list_string_constants.
    // "SAVEFILE" appears in two different methods (reverse-lookup must find both);
    // LoadGame additionally emits a unique key so a single-hit search is verifiable.
    public static class StringKeys
    {
        public static string SaveGame()
        {
            System.Console.WriteLine("SAVEFILE");
            return "PlayerPrefs.Score";
        }

        public static string LoadGame()
        {
            System.Console.WriteLine("SAVEFILE");
            return "TheFinaleUmbra";
        }

        public static string NoStrings(int x) => (x + 1).ToString();
    }

    // Cross-reference coverage for find_callers / find_references.
    // - sceneToLoad is read by GetScene (ldsfld) and written by SetScene (stsfld).
    // - CallsAddOne calls Simple.AddOne (find_callers target).
    // - RefType emits ldtoken of Simple (type reference).
    public static class Refs
    {
        public static int sceneToLoad;

        public static void SetScene(int v) { sceneToLoad = v; }
        public static int GetScene() => sceneToLoad;

        public static int CallsAddOne() => Simple.AddOne(5);
        public static string CallsSave() => StringKeys.SaveGame();
        public static System.Type RefType() => typeof(Simple);

        // find_callees ("Uses") coverage: this body references methods (AddOne, Add) and a
        // field (sceneToLoad, read + written), so the callee list spans more than one ref kind.
        public static int Uses()
        {
            sceneToLoad = Simple.AddOne(sceneToLoad);
            return Simple.Add(sceneToLoad, 1);
        }
    }

    // Property + event coverage for search_members kinds (the fixture otherwise has only
    // methods and fields). Health/Title are properties; OnDied is an event (invoked from Die
    // so the compiler-generated backing field isn't flagged unused).
    public class Members
    {
        public int Health { get; set; }
        public static string Title { get; set; }

        public event System.Action OnDied;
        public void Die() => OnDied?.Invoke();
    }

    // Base-type hierarchy for list_types base_type filtering (incl. transitive: Boss : Enemy : BaseEntity)
    // and find_overrides: Attack is virtual on BaseEntity and overridden down the chain
    // (Boss overrides Enemy's override, which overrides BaseEntity's).
    public abstract class BaseEntity { public virtual int Attack() => 1; }
    public class Player : BaseEntity { public override int Attack() => 100; }
    public class Enemy : BaseEntity { public override int Attack() => 50; }
    public class Boss : Enemy { public override int Attack() => 500; }

    // Compiler-generated state-machine coverage for nested-type addressing + decompile rescue.
    // DoCoroutine -> nested iterator state machine <DoCoroutine>d__N : IEnumerator (MoveNext).
    // DoAsync     -> nested async state machine     <DoAsync>d__N : IAsyncStateMachine (MoveNext).
    public class Machines
    {
        public static int counter;

        public System.Collections.IEnumerator DoCoroutine()
        {
            counter++;
            yield return null;
            counter += 10;
        }

        public async void DoAsync()
        {
            await System.Threading.Tasks.Task.Yield();
            counter += 100;
        }
    }
}
