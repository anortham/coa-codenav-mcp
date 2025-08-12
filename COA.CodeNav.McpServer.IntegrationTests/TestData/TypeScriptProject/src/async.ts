/**
 * Async operations for testing
 */

// Promise-based function
export async function fetchData(url: string): Promise<any> {
    const response = await fetch(url);
    if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`);
    }
    return await response.json();
}

// Async function with proper error handling
export async function safeAsyncOperation(): Promise<void> {
    try {
        const data = await fetchData('https://api.example.com/data');
        console.log('Data received:', data);
    } catch (error) {
        console.error('Error fetching data:', error);
    }
}

// Function that returns a promise
export function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

// Async generator function
export async function* asyncGenerator(): AsyncGenerator<number> {
    for (let i = 0; i < 5; i++) {
        await delay(100);
        yield i;
    }
}

// Function with async forEach anti-pattern (will cause issues)
export async function asyncOperation(): Promise<void> {
    const items = [1, 2, 3, 4, 5];
    
    // This is problematic - forEach doesn't wait for async operations
    items.forEach(async (item) => {
        await delay(100);
        console.log(item);
    });
    
    // Should use for...of or Promise.all instead
}

// Proper async iteration
export async function properAsyncIteration(): Promise<void> {
    const items = [1, 2, 3, 4, 5];
    
    // Sequential processing
    for (const item of items) {
        await delay(100);
        console.log(item);
    }
    
    // Or parallel processing
    await Promise.all(items.map(async (item) => {
        await delay(100);
        console.log(item);
    }));
}

// Type for async functions
export type AsyncFunction<T> = () => Promise<T>;

// Interface with async method
export interface DataService {
    getData(): Promise<any>;
    saveData(data: any): Promise<void>;
    deleteData(id: string): Promise<boolean>;
}