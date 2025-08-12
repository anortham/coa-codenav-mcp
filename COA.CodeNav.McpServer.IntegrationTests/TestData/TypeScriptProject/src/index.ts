// Main entry point for the TypeScript test project
import { Calculator } from './calculator';
import { User, UserService } from './userService';
import { asyncOperation } from './async';

// This will cause an error - unused variable
const unusedVariable = 42;

export class Application {
    private calculator: Calculator;
    private userService: UserService;

    constructor() {
        this.calculator = new Calculator();
        this.userService = new UserService();
    }

    public run(): void {
        // Test calculator
        const result = this.calculator.add(5, 3);
        console.log(`5 + 3 = ${result}`);

        // Test user service
        const user: User = {
            id: 1,
            name: 'John Doe',
            email: 'john@example.com'
        };
        
        this.userService.addUser(user);
        
        // This will cause a type error - wrong type
        const wrongTypeUser = {
            id: '2', // Should be number
            name: 'Jane Doe',
            email: 'jane@example.com'
        };
        
        // @ts-expect-error - Intentional type error for testing
        this.userService.addUser(wrongTypeUser);
    }

    // Method with unreachable code
    public testMethod(value: number): string {
        if (value > 0) {
            return 'positive';
        } else {
            return 'non-positive';
        }
        // This will cause an error - unreachable code
        console.log('This is unreachable');
    }
}

// Missing return statement
function brokenFunction(x: number): number {
    if (x > 0) {
        return x * 2;
    }
    // Missing return for else case - will cause error with noImplicitReturns
}

const app = new Application();
app.run();