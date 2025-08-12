/**
 * A simple calculator class for testing TypeScript analysis
 */
export class Calculator {
    /**
     * Adds two numbers
     * @param a First number
     * @param b Second number
     * @returns The sum of a and b
     */
    public add(a: number, b: number): number {
        return a + b;
    }

    /**
     * Subtracts b from a
     * @param a First number
     * @param b Second number
     * @returns The difference of a and b
     */
    public subtract(a: number, b: number): number {
        return a - b;
    }

    /**
     * Multiplies two numbers
     * @param a First number
     * @param b Second number
     * @returns The product of a and b
     */
    public multiply(a: number, b: number): number {
        return a * b;
    }

    /**
     * Divides a by b
     * @param a Numerator
     * @param b Denominator
     * @returns The quotient of a and b
     * @throws Error if b is zero
     */
    public divide(a: number, b: number): number {
        if (b === 0) {
            throw new Error('Division by zero');
        }
        return a / b;
    }

    // Method with unused parameter - will trigger warning
    public unusedParameterMethod(used: number, unused: number): number {
        return used * 2;
    }
}

// Interface for testing
export interface CalculatorOptions {
    precision?: number;
    roundingMode?: 'up' | 'down' | 'nearest';
}

// Type alias
export type CalculationResult = {
    value: number;
    timestamp: Date;
    operation: string;
};