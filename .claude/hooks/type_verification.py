#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#     "regex",
# ]
# ///

"""
Type Verification Hook for Claude Code

This PreToolUse hook enforces type verification by blocking Edit/Write/MultiEdit
operations that use unverified types. It forces Claude to use CodeNav tools
to verify type definitions before generating code.

Key Features:
- System-level enforcement (cannot be bypassed by Claude)
- Session-based verification cache
- Clear guidance on which verification tools to use
- Support for both C# and TypeScript patterns
"""

import json
import sys
import re
import os
from pathlib import Path
from typing import Dict, Set, List, Optional
from datetime import datetime, timedelta
import argparse


class TypeVerificationHook:
    """Main class for type verification enforcement."""
    
    def __init__(self, mode: str = "strict"):
        self.mode = mode  # "strict", "warn", or "disabled"
        self.cache_path = Path.cwd() / ".claude" / "data" / "verified_types.json"
        self.log_path = Path.cwd() / "logs" / "type_verification.json"
        
        # Ensure directories exist
        self.cache_path.parent.mkdir(parents=True, exist_ok=True)
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
    
    def load_verified_types(self) -> Dict:
        """Load the verification cache."""
        if not self.cache_path.exists():
            return {"session_id": "", "verified_types": {}}
        
        try:
            with open(self.cache_path, 'r') as f:
                return json.load(f)
        except (json.JSONDecodeError, Exception):
            return {"session_id": "", "verified_types": {}}
    
    def save_verified_types(self, cache_data: Dict):
        """Save the verification cache."""
        try:
            with open(self.cache_path, 'w') as f:
                json.dump(cache_data, f, indent=2)
        except Exception:
            pass  # Fail silently to avoid breaking the hook
    
    def log_event(self, event_type: str, data: Dict):
        """Log verification events."""
        log_entry = {
            "timestamp": datetime.utcnow().isoformat(),
            "event_type": event_type,
            "data": data
        }
        
        try:
            # Read existing log
            log_data = []
            if self.log_path.exists():
                with open(self.log_path, 'r') as f:
                    try:
                        log_data = json.load(f)
                    except json.JSONDecodeError:
                        log_data = []
            
            # Append new entry
            log_data.append(log_entry)
            
            # Keep only last 1000 entries
            if len(log_data) > 1000:
                log_data = log_data[-1000:]
            
            # Write back
            with open(self.log_path, 'w') as f:
                json.dump(log_data, f, indent=2)
        except Exception:
            pass  # Fail silently
    
    def get_bcl_whitelist(self) -> Set[str]:
        """Get whitelisted types that don't need verification."""
        return {
            # C# Basic types
            "string", "int", "bool", "double", "float", "decimal", "long",
            "short", "byte", "char", "object", "void", "var", "dynamic",
            
            # C# Common BCL types
            "String", "Int32", "Boolean", "Double", "Single", "Decimal",
            "DateTime", "TimeSpan", "Guid", "Exception", "ArgumentException",
            "InvalidOperationException", "NotImplementedException",
            "List", "Dictionary", "IEnumerable", "ICollection", "IList",
            "Array", "StringBuilder", "Task", "CancellationToken",
            
            # TypeScript built-in types
            "number", "boolean", "undefined", "null", "any", "unknown",
            "never", "symbol", "bigint",
            
            # TypeScript common types
            "Array", "Date", "RegExp", "Error", "Promise", "Map", "Set",
            "WeakMap", "WeakSet", "JSON", "Math", "Console",
            
            # Common utility types
            "Partial", "Required", "Readonly", "Record", "Pick", "Omit",
            "Exclude", "Extract", "NonNullable", "ReturnType", "Parameters"
        }
    
    def extract_types_from_code(self, code: str, language: str = "auto") -> Set[str]:
        """Extract type references from code."""
        if not code or not code.strip():
            return set()
        
        types = set()
        
        # Detect language if auto
        if language == "auto":
            if any(keyword in code for keyword in ["class ", "interface ", "namespace ", "using "]):
                language = "csharp"
            elif any(keyword in code for keyword in ["interface ", "type ", "const ", "let "]):
                language = "typescript"
            else:
                language = "csharp"  # Default assumption
        
        if language == "csharp":
            types.update(self._extract_csharp_types(code))
        elif language == "typescript":
            types.update(self._extract_typescript_types(code))
        
        # Filter out whitelisted types
        whitelist = self.get_bcl_whitelist()
        return {t for t in types if t not in whitelist and len(t) > 1}
    
    def _extract_csharp_types(self, code: str) -> Set[str]:
        """Extract C# type references."""
        types = set()
        
        # Common C# patterns
        patterns = [
            r'\bnew\s+([A-Z]\w*)',  # new User()
            r'\b([A-Z]\w*)\s+\w+\s*[=;]',  # User user = 
            r'\b([A-Z]\w*)\?\s+\w+',  # User? user
            r':\s*([A-Z]\w*)',  # inheritance : BaseClass
            r'<([A-Z]\w*)>',  # generics List<User>
            r'<([A-Z]\w*),',  # Dictionary<User, 
            r',\s*([A-Z]\w*)>',  # Dictionary<string, User>
            r'\b([A-Z]\w*)\.',  # User.Property
            r'typeof\(([A-Z]\w*)\)',  # typeof(User)
            r'is\s+([A-Z]\w*)',  # is User
            r'as\s+([A-Z]\w*)',  # as User
            r'return\s+([A-Z]\w*)\.',  # return User.
            r'\(([A-Z]\w*)\s+\w+\)',  # method parameter (User user)
        ]
        
        for pattern in patterns:
            matches = re.finditer(pattern, code)
            for match in matches:
                types.add(match.group(1))
        
        return types
    
    def _extract_typescript_types(self, code: str) -> Set[str]:
        """Extract TypeScript type references."""
        types = set()
        
        # TypeScript patterns
        patterns = [
            r':\s*([A-Z]\w*)',  # type annotation : User
            r'as\s+([A-Z]\w*)',  # type assertion as User  
            r'\bnew\s+([A-Z]\w*)',  # new User()
            r'<([A-Z]\w*)>',  # generics Array<User>
            r'<([A-Z]\w*),',  # Map<User,
            r',\s*([A-Z]\w*)>',  # Map<string, User>
            r'\b([A-Z]\w*)\.',  # User.property
            r'instanceof\s+([A-Z]\w*)',  # instanceof User
            r'interface\s+([A-Z]\w*)',  # interface User
            r'type\s+([A-Z]\w*)',  # type User
            r'class\s+([A-Z]\w*)',  # class User
            r'extends\s+([A-Z]\w*)',  # extends BaseUser
            r'implements\s+([A-Z]\w*)',  # implements IUser
        ]
        
        for pattern in patterns:
            matches = re.finditer(pattern, code)
            for match in matches:
                types.add(match.group(1))
        
        return types
    
    def check_verification_status(self, types: Set[str], session_id: str) -> Dict:
        """Check which types are verified and which need verification."""
        cache_data = self.load_verified_types()
        
        # If session changed, clear cache (new session)
        if cache_data.get("session_id") != session_id:
            cache_data = {"session_id": session_id, "verified_types": {}}
            self.save_verified_types(cache_data)
        
        verified_types = cache_data.get("verified_types", {})
        
        verified = set()
        unverified = set()
        
        for type_name in types:
            type_info = verified_types.get(type_name)
            if type_info:
                # Check if verification is still valid based on file modification
                if self._is_verification_still_valid(type_info):
                    verified.add(type_name)
                    continue
            
            unverified.add(type_name)
        
        return {
            "verified": verified,
            "unverified": unverified,
            "total": len(types)
        }
    
    def _is_verification_still_valid(self, type_info: Dict) -> bool:
        """Check if a type verification is still valid based on file modification times."""
        try:
            # Get the source file for this type
            source_file = type_info.get("file") or type_info.get("source_file")
            if not source_file:
                # No source file info, fall back to time-based check (7 days instead of 24 hours)
                verified_at = datetime.fromisoformat(type_info.get("verified_at", "1970-01-01"))
                return datetime.utcnow() - verified_at < timedelta(days=7)
            
            # Check if the source file still exists
            source_path = Path(source_file)
            if not source_path.exists():
                # File was deleted, invalidate
                return False
            
            # Get current file modification time
            current_mtime = source_path.stat().st_mtime
            
            # Get cached modification time
            cached_mtime = type_info.get("file_mtime")
            if cached_mtime is None:
                # No cached mtime, type was verified before file tracking - invalidate to re-verify
                return False
            
            # Compare modification times
            if current_mtime > cached_mtime:
                # File was modified since verification, invalidate
                return False
            
            # File unchanged, verification still valid
            return True
            
        except (OSError, ValueError, TypeError):
            # Any error checking file, fall back to time-based check
            verified_at = datetime.fromisoformat(type_info.get("verified_at", "1970-01-01"))
            return datetime.utcnow() - verified_at < timedelta(days=7)
    
    def get_verification_tools(self, types: Set[str], file_path: str = "") -> List[str]:
        """Get recommended verification tools for the given types."""
        if not types:
            return []
        
        # Determine language from file path
        is_csharp = file_path.endswith(('.cs', '.csx'))
        is_typescript = file_path.endswith(('.ts', '.tsx', '.js', '.jsx'))
        
        tools = []
        
        if is_csharp or (not is_typescript):
            # C# verification tools
            tools.extend([
                f"csharp_hover for {', '.join(sorted(types)[:3])}{'...' if len(types) > 3 else ''}",
                "csharp_goto_definition to see actual implementations",
                "csharp_symbol_search to find type definitions"
            ])
        
        if is_typescript or (not is_csharp):
            # TypeScript verification tools
            tools.extend([
                f"ts_hover for {', '.join(sorted(types)[:3])}{'...' if len(types) > 3 else ''}",
                "ts_goto_definition to see type structure",
                "ts_document_symbols to understand file contents"
            ])
        
        return tools
    
    def process_tool_call(self, tool_name: str, tool_input: Dict, session_id: str) -> Optional[str]:
        """Process a tool call and return error message if should be blocked."""
        
        # Only process edit tools
        if tool_name not in ["Edit", "Write", "MultiEdit"]:
            return None
        
        # Extract code being written
        if tool_name == "Edit":
            new_code = tool_input.get("new_string", "")
            file_path = tool_input.get("file_path", "")
        elif tool_name == "Write":
            new_code = tool_input.get("content", "")
            file_path = tool_input.get("file_path", "")
        elif tool_name == "MultiEdit":
            # Handle multiple edits
            edits = tool_input.get("edits", [])
            new_code = " ".join(edit.get("new_string", "") for edit in edits)
            file_path = tool_input.get("file_path", "")
        else:
            return None
        
        # Extract types from the code
        types = self.extract_types_from_code(new_code)
        if not types:
            return None  # No types to verify
        
        # Check verification status
        verification_status = self.check_verification_status(types, session_id)
        unverified_types = verification_status["unverified"]
        
        if not unverified_types:
            # All types verified, log success and allow
            self.log_event("allowed", {
                "tool_name": tool_name,
                "file_path": file_path,
                "verified_types": list(verification_status["verified"]),
                "total_types": verification_status["total"]
            })
            return None
        
        # Some types are unverified
        tools_needed = self.get_verification_tools(unverified_types, file_path)
        
        error_msg = f"BLOCKED: Unverified types detected: {', '.join(sorted(unverified_types))}\n"
        error_msg += f"You must verify these types first using:\n"
        for tool in tools_needed[:2]:  # Limit to 2 suggestions to avoid overwhelming
            error_msg += f"• {tool}\n"
        error_msg += f"Then retry the {tool_name.lower()} operation."
        
        # Log the block
        self.log_event("blocked", {
            "tool_name": tool_name,
            "file_path": file_path,
            "unverified_types": list(unverified_types),
            "verified_types": list(verification_status["verified"]),
            "total_types": verification_status["total"],
            "suggested_tools": tools_needed
        })
        
        return error_msg


def main():
    parser = argparse.ArgumentParser(description="Type Verification Hook")
    parser.add_argument("--mode", choices=["strict", "warn", "disabled"], 
                       default=os.environ.get("TYPE_VERIFY_MODE", "strict"),
                       help="Verification mode")
    parser.add_argument("--log-only", action="store_true",
                       help="Log events but don't block (same as --mode warn)")
    
    args = parser.parse_args()
    
    if args.log_only:
        args.mode = "warn"
    
    try:
        # Read JSON input from stdin
        input_data = json.load(sys.stdin)
        
        tool_name = input_data.get('tool_name', '')
        tool_input = input_data.get('tool_input', {})
        session_id = input_data.get('session_id', 'default')
        
        # Initialize hook
        hook = TypeVerificationHook(mode=args.mode)
        
        if args.mode == "disabled":
            sys.exit(0)  # Allow all operations
        
        # Process the tool call
        error_msg = hook.process_tool_call(tool_name, tool_input, session_id)
        
        if error_msg:
            if args.mode == "strict":
                # Block the operation
                print(error_msg, file=sys.stderr)
                sys.exit(2)  # Exit code 2 blocks the tool call
            else:  # warn mode
                # Just log the warning
                print(f"WARNING: {error_msg}", file=sys.stderr)
                sys.exit(0)  # Allow the operation
        else:
            # Allow the operation
            sys.exit(0)
        
    except json.JSONDecodeError:
        # Invalid JSON, allow operation to continue
        sys.exit(0)
    except Exception as e:
        # Any other error, allow operation to continue to avoid breaking workflow
        sys.exit(0)


if __name__ == '__main__':
    main()