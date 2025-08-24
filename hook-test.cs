// Test if PowerShell type verification hooks are working
User user = new User();
user.FullName = "John Doe";
UserService service = new UserService();
var result = await service.GetUserByIdAsync(123);