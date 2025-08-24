// Test file to trigger hooks with unverified types
using System;

public class TestClass 
{
    public string Name { get; set; }
    
    public void DoSomething()
    {
        // Test with only verified types
        var test = new TestClass();
        test.Name = "Verified Type Test";
        
        // Use standard .NET types (should be allowed)
        var message = "Hello World";
        var length = message.Length;
    }
}