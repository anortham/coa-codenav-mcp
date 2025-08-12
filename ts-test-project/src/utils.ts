// Utility functions for testing cross-file navigation
import { User } from './index';

export function validateEmail(email: string): boolean {
    const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
    return emailRegex.test(email);
}

export function createUser(name: string, email: string, id?: number): User {
    if (!validateEmail(email)) {
        throw new Error('Invalid email format');
    }
    
    return {
        id: id || Math.floor(Math.random() * 1000),
        name,
        email
    };
}

// Test cross-references
export const DEFAULT_USER: User = {
    id: 0,
    name: "Anonymous",
    email: "anonymous@example.com"
};