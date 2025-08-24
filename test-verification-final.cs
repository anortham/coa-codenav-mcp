// Test hook blocking with completely unverified types after restart
UnknownClass unknown = new UnknownClass();
unknown.InvalidProperty = "this should trigger hooks";
var result = unknown.FakeMethod();