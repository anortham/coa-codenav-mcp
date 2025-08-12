/**
 * User management service for testing
 */

export interface User {
    id: number;
    name: string;
    email: string;
    age?: number;
    isActive?: boolean;
}

export class UserService {
    private users: Map<number, User> = new Map();

    /**
     * Adds a new user
     * @param user The user to add
     */
    public addUser(user: User): void {
        if (this.users.has(user.id)) {
            throw new Error(`User with id ${user.id} already exists`);
        }
        this.users.set(user.id, user);
    }

    /**
     * Gets a user by ID
     * @param id The user ID
     * @returns The user or undefined if not found
     */
    public getUser(id: number): User | undefined {
        return this.users.get(id);
    }

    /**
     * Updates a user
     * @param id The user ID
     * @param updates Partial user updates
     */
    public updateUser(id: number, updates: Partial<User>): void {
        const user = this.users.get(id);
        if (!user) {
            throw new Error(`User with id ${id} not found`);
        }
        
        // Spread operator to merge updates
        const updatedUser = { ...user, ...updates };
        this.users.set(id, updatedUser);
    }

    /**
     * Deletes a user
     * @param id The user ID
     * @returns true if deleted, false if not found
     */
    public deleteUser(id: number): boolean {
        return this.users.delete(id);
    }

    /**
     * Gets all users
     * @returns Array of all users
     */
    public getAllUsers(): User[] {
        return Array.from(this.users.values());
    }

    /**
     * Finds users by name
     * @param name The name to search for
     * @returns Array of matching users
     */
    public findUsersByName(name: string): User[] {
        return this.getAllUsers().filter(user => 
            user.name.toLowerCase().includes(name.toLowerCase())
        );
    }

    // Method with potential null reference - for testing
    public riskyMethod(user: User | null): string {
        // This could cause a null reference error if strict null checks are on
        return user.name.toUpperCase();
    }
}

// Enum for testing
export enum UserRole {
    Admin = 'ADMIN',
    User = 'USER',
    Guest = 'GUEST'
}

// Generic interface for testing
export interface Repository<T> {
    add(item: T): void;
    get(id: number): T | undefined;
    update(id: number, item: T): void;
    delete(id: number): boolean;
    getAll(): T[];
}