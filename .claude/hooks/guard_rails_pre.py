#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#     "regex",
# ]
# ///

"""
Guard Rails PreToolUse Hook

Provides gentle guidance to verify types before Edit/Write/MultiEdit operations.
Never blocks operations, just reminds Claude to use CodeNav tools for better
type verification. Works with session state to avoid redundant suggestions.
"""

import json
import sys
import re
import os
from pathlib import Path
from typing import Set, List, Optional, Dict
from datetime import datetime
import argparse


class GuardRailsPreHook:
    """Provides gentle pre-edit guidance for type verification."""
    
    def __init__(self, workspace_path: str):
        self.workspace_path = Path(workspace_path)
        self.state_file = self.workspace_path / ".claude" / "data" / "session_state.json"
        self.logs_dir = self.workspace_path / "logs"
        
        # Ensure directories exist
        self.logs_dir.mkdir(exist_ok=True)
    
    def load_session_state(self) -> Optional[Dict]:
        """Load current session state."""
        if not self.state_file.exists():
            return None
        
        try:
            with open(self.state_file, 'r') as f:
                return json.load(f)
        except (json.JSONDecodeError, Exception):
            return None
    
    def get_common_types_whitelist(self) -> Set[str]:
        """Get types that don't need verification."""
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
            "ILogger", "IConfiguration", "IServiceCollection",
            
            # TypeScript built-in types
            "number", "boolean", "undefined", "null", "any", "unknown",
            "never", "symbol", "bigint",
            
            # TypeScript common types
            "Array", "Date", "RegExp", "Error", "Promise", "Map", "Set",
            "WeakMap", "WeakSet", "JSON", "Math", "Console",
            
            # Common utility types
            "Partial", "Required", "Readonly", "Record", "Pick", "Omit",
            "Exclude", "Extract", "NonNullable", "ReturnType", "Parameters",
            
            # Common NPM types
            "Component", "FC", "ReactNode", "JSX", "useState", "useEffect",
            "Request", "Response", "NextFunction", "Express"
        }
    
    def extract_types_from_code(self, code: str, file_path: str = "") -> Set[str]:
        """Extract type references from code being edited."""
        if not code or not code.strip():
            return set()
        
        types = set()
        
        # Determine language
        is_csharp = file_path.endswith(('.cs', '.csx')) or any(
            keyword in code for keyword in ["using ", "namespace ", "class ", "public class"]
        )
        is_typescript = file_path.endswith(('.ts', '.tsx', '.js', '.jsx')) or any(
            keyword in code for keyword in ["interface ", "type ", "import ", "export "]
        )
        
        if is_csharp or (not is_typescript):
            types.update(self._extract_csharp_types(code))
        
        if is_typescript or (not is_csharp):
            types.update(self._extract_typescript_types(code))
        
        # Filter out whitelisted types and short names
        whitelist = self.get_common_types_whitelist()
        return {t for t in types if t not in whitelist and len(t) > 2 and t[0].isupper()}
    
    def _extract_csharp_types(self, code: str) -> Set[str]:
        """Extract C# type references."""
        types = set()
        
        patterns = [
            r'\\bnew\\s+([A-Z]\\w*)',          # new User()
            r'\\b([A-Z]\\w*)\\s+\\w+\\s*[=;]',  # User user = 
            r'\\b([A-Z]\\w*)\\?\\s+\\w+',       # User? user
            r':\\s*([A-Z]\\w*)',              # inheritance : BaseClass
            r'<([A-Z]\\w*)>',                 # generics List<User>
            r'<([A-Z]\\w*),',                 # Dictionary<User, 
            r',\\s*([A-Z]\\w*)>',             # Dictionary<string, User>
            r'\\b([A-Z]\\w*)\\.',             # User.Property
            r'typeof\\(([A-Z]\\w*)\\)',       # typeof(User)
            r'is\\s+([A-Z]\\w*)',             # is User
            r'as\\s+([A-Z]\\w*)',             # as User
            r'\\(([A-Z]\\w*)\\s+\\w+\\)',     # method parameter (User user)
        ]
        
        for pattern in patterns:
            matches = re.finditer(pattern, code, re.IGNORECASE)
            for match in matches:
                type_name = match.group(1)
                if type_name and len(type_name) > 2:
                    types.add(type_name)
        
        return types
    
    def _extract_typescript_types(self, code: str) -> Set[str]:
        """Extract TypeScript type references."""
        types = set()
        
        patterns = [
            r':\\s*([A-Z]\\w*)',              # type annotation : User
            r'as\\s+([A-Z]\\w*)',             # type assertion as User  
            r'\\bnew\\s+([A-Z]\\w*)',         # new User()
            r'<([A-Z]\\w*)>',                 # generics Array<User>
            r'<([A-Z]\\w*),',                 # Map<User,
            r',\\s*([A-Z]\\w*)>',             # Map<string, User>
            r'\\b([A-Z]\\w*)\\.',             # User.property
            r'instanceof\\s+([A-Z]\\w*)',     # instanceof User
            r'interface\\s+([A-Z]\\w*)',      # interface User
            r'type\\s+([A-Z]\\w*)',           # type User
            r'class\\s+([A-Z]\\w*)',          # class User
            r'extends\\s+([A-Z]\\w*)',        # extends BaseUser
            r'implements\\s+([A-Z]\\w*)',     # implements IUser
        ]
        
        for pattern in patterns:
            matches = re.finditer(pattern, code, re.IGNORECASE)
            for match in matches:
                type_name = match.group(1)
                if type_name and len(type_name) > 2:
                    types.add(type_name)
        
        return types
    
    def check_auto_loading_status(self) -> dict:
        """Check if auto-loading is enabled and working."""
        # In a real implementation, this could check MCP server status
        # For now, assume auto-loading is working based on our recent implementation
        return {
            "csharp_auto_loaded": True,
            "typescript_auto_loaded": True,
            "auto_loading_enabled": True
        }
    
    def get_verification_suggestions(self, types: Set[str], file_path: str, state: Optional[Dict]) -> List[str]:
        """Generate specific verification suggestions."""
        if not types:
            return []
        
        suggestions = []
        auto_status = self.check_auto_loading_status()
        
        # Check what's already verified in this session
        verified_csharp = set()
        verified_typescript = set()
        
        if state:
            verified_csharp = set(state.get("csharp", {}).get("verified_types", []))
            verified_typescript = set(state.get("typescript", {}).get("verified_types", []))
        
        unverified_types = types - verified_csharp - verified_typescript
        
        if not unverified_types:
            return []  # All types already verified this session
        
        # Determine file type and give appropriate suggestions
        is_csharp = file_path.endswith(('.cs', '.csx'))
        is_typescript = file_path.endswith(('.ts', '.tsx', '.js', '.jsx'))
        
        if is_csharp or (not is_typescript):
            # C# suggestions
            solution_loaded = (state and state.get("csharp", {}).get("solution_loaded", False)) or auto_status["csharp_auto_loaded"]
            
            if not solution_loaded:
                suggestions.append("📌 Load solution first for C# type verification")
                if state and state.get("project_detection", {}).get("csharp", {}).get("solutions"):
                    solution_path = state["project_detection"]["csharp"]["solutions"][0]
                    suggestions.append(f"   mcp__codenav__csharp_load_solution {solution_path}")
                suggestions.append("")
            else:
                # Solution is loaded (via auto-loading or manual), focus on type verification
                type_list = sorted(list(unverified_types))[:3]  # Show max 3 types
                suggestions.append(f"💡 Verify C# types: {', '.join(type_list)}")
                for t in type_list:
                    suggestions.append(f"   mcp__codenav__csharp_hover {file_path} <line> <col> → {t} details")
        
        if is_typescript or (not is_csharp):
            # TypeScript suggestions - auto-loading should make these ready
            if auto_status["typescript_auto_loaded"]:
                type_list = sorted(list(unverified_types))[:3]
                suggestions.append(f"💡 Verify TypeScript types: {', '.join(type_list)}")
                for t in type_list:
                    suggestions.append(f"   mcp__codenav__ts_hover {file_path} <line> <col> → {t} structure")
            else:
                suggestions.append("💡 TypeScript verification available (auto-configures from tsconfig.json)")
        
        return suggestions
    
    def log_guidance_event(self, tool_name: str, types: Set[str], suggestions: List[str]):
        """Log when guidance is provided."""
        log_entry = {
            "timestamp": datetime.utcnow().isoformat(),
            "event": "pre_edit_guidance",
            "tool_name": tool_name,
            "types_detected": sorted(list(types)),
            "suggestions_provided": len(suggestions) > 0
        }
        
        try:
            log_file = self.logs_dir / "guard_rails_pre.json"
            log_data = []
            
            if log_file.exists():
                with open(log_file, 'r') as f:
                    try:
                        log_data = json.load(f)
                    except json.JSONDecodeError:
                        log_data = []
            
            log_data.append(log_entry)
            
            # Keep last 500 entries
            if len(log_data) > 500:
                log_data = log_data[-500:]
            
            with open(log_file, 'w') as f:
                json.dump(log_data, f, indent=2)
        except Exception:
            pass
    
    def should_provide_guidance(self, tool_name: str, types: Set[str]) -> bool:
        """Determine if guidance should be provided."""
        # Only guide for edit operations with detected types
        if tool_name not in ["Edit", "Write", "MultiEdit"]:
            return False
        
        if not types:
            return False
        
        return True
    
    def process_tool_call(self, tool_name: str, tool_input: Dict, session_id: str):
        """Process the tool call and provide guidance if needed."""
        
        # Extract code and file path
        file_path = ""
        code_content = ""
        
        if tool_name == "Edit":
            file_path = tool_input.get("file_path", "")
            code_content = tool_input.get("new_string", "")
        elif tool_name == "Write":
            file_path = tool_input.get("file_path", "")
            code_content = tool_input.get("content", "")
        elif tool_name == "MultiEdit":
            file_path = tool_input.get("file_path", "")
            edits = tool_input.get("edits", [])
            code_content = " ".join(edit.get("new_string", "") for edit in edits)
        
        # Extract types from the code
        types = self.extract_types_from_code(code_content, file_path)
        
        # Check if we should provide guidance
        if not self.should_provide_guidance(tool_name, types):
            return
        
        # Load session state
        state = self.load_session_state()
        
        # Get verification suggestions
        suggestions = self.get_verification_suggestions(types, file_path, state)
        
        if suggestions:
            print("💡 Type Verification Suggestion:", file=sys.stderr)
            for suggestion in suggestions:
                print(f"   {suggestion}", file=sys.stderr)
            print("", file=sys.stderr)
        
        # Log the event
        self.log_guidance_event(tool_name, types, suggestions)


def main():
    parser = argparse.ArgumentParser(description="Guard Rails PreToolUse Hook")
    parser.add_argument("--workspace", type=str, help="Override workspace path")
    
    args = parser.parse_args()
    
    try:
        # Read JSON input from stdin
        input_data = json.load(sys.stdin)
        
        tool_name = input_data.get('tool_name', '')
        tool_input = input_data.get('tool_input', {})
        session_id = input_data.get('session_id', 'unknown')
        cwd = input_data.get('cwd', os.getcwd())
        
        # Use provided workspace or detect from input
        workspace_path = args.workspace or cwd
        
        # Initialize hook
        hook = GuardRailsPreHook(workspace_path)
        
        # Process the tool call
        hook.process_tool_call(tool_name, tool_input, session_id)
        
        # Always allow the operation to proceed
        sys.exit(0)
        
    except json.JSONDecodeError:
        # Invalid JSON, allow operation to continue
        sys.exit(0)
    except Exception as e:
        # Any error, allow operation to continue
        sys.exit(0)


if __name__ == '__main__':
    main()