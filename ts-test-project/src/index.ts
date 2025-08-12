// Test file for TypeScript navigation
export interface User {
    id: number;
    name: string;
    email: string;
}

export class UserService {
    private users: User[] = [];

    constructor() {
        this.users = [];
    }

    public addUser(user: User): void {
        this.users.push(user);
    }

    public findUserById(id: number): User | undefined {
        return this.users.find(user => user.id === id);
    }

    public getAllUsers(): User[] {
        return [...this.users];
    }
}

// Usage example
const userService = new UserService();
const newUser: User = {
    id: 1,
    name: "John Doe",
    email: "john@example.com"
};

userService.addUser(newUser);
const foundUser = userService.findUserById(1);

// Intentional error for testing diagnostics
let unusedVariable = "this should trigger a warning";
console.log(foundUser?.name);