#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#     "regex",
# ]
# ///

"""
Guard Rails PostToolUse Hook

Tracks successful use of CodeNav tools, updates session state, and provides
positive reinforcement. Builds session-local knowledge about verified types
and solution loading status.
"""

import json
import sys
import re
import os
from pathlib import Path
from typing import Dict, List, Optional, Any
from datetime import datetime
import argparse


class GuardRailsPostHook:
    """Tracks successful tool usage and builds session knowledge."""
    
    def __init__(self, workspace_path: str):
        self.workspace_path = Path(workspace_path)
        self.state_file = self.workspace_path / ".claude" / "data" / "session_state.json"
        self.logs_dir = self.workspace_path / "logs"
        
        # Ensure directories exist
        self.state_file.parent.mkdir(parents=True, exist_ok=True)
        self.logs_dir.mkdir(exist_ok=True)
    
    def load_session_state(self) -> Dict:
        """Load current session state, creating default if needed."""
        if not self.state_file.exists():
            return self._create_default_state()
        
        try:
            with open(self.state_file, 'r') as f:
                state = json.load(f)
                # Ensure all required keys exist
                return self._ensure_state_structure(state)
        except (json.JSONDecodeError, Exception):
            return self._create_default_state()
    
    def _create_default_state(self) -> Dict:
        """Create default session state structure."""
        return {
            "session_id": "unknown",
            "created_at": datetime.utcnow().isoformat(),
            "workspace_path": str(self.workspace_path),
            "project_detection": {
                "csharp": {"detected": False, "solutions": [], "projects": []},
                "typescript": {"detected": False, "configs": [], "packages": []}
            },
            "csharp": {
                "solution_loaded": False,
                "solution_path": None,
                "verified_types": []
            },
            "typescript": {
                "workspace_loaded": False,
                "verified_types": []
            },
            "statistics": {
                "total_verifications": 0,
                "session_start": datetime.utcnow().isoformat()
            }
        }
    
    def _ensure_state_structure(self, state: Dict) -> Dict:
        """Ensure state has all required keys."""
        default = self._create_default_state()
        
        # Merge with defaults for any missing keys
        for key, value in default.items():
            if key not in state:
                state[key] = value
            elif isinstance(value, dict):
                for subkey, subvalue in value.items():
                    if subkey not in state[key]:
                        state[key][subkey] = subvalue
        
        return state
    
    def save_session_state(self, state: Dict):
        """Save session state to file."""
        try:
            state["last_updated"] = datetime.utcnow().isoformat()
            with open(self.state_file, 'w') as f:
                json.dump(state, f, indent=2)
        except Exception:
            pass  # Fail silently
    
    def extract_type_from_response(self, tool_response: Any) -> Optional[str]:
        """Extract type name from tool response."""
        if not tool_response:
            return None
        
        response_text = str(tool_response)
        
        # Look for common patterns in hover/definition responses
        patterns = [
            r'class\\s+([A-Z]\\w*)',           # class User
            r'interface\\s+([A-Z]\\w*)',       # interface IUser
            r'struct\\s+([A-Z]\\w*)',          # struct Point
            r'enum\\s+([A-Z]\\w*)',            # enum Status
            r'type\\s+([A-Z]\\w*)',            # type User (TypeScript)
        ]
        
        for pattern in patterns:
            match = re.search(pattern, response_text, re.IGNORECASE)
            if match:
                return match.group(1)
        
        return None
    
    def extract_type_members(self, tool_response: Any) -> Dict[str, List[str]]:
        """Extract type members (properties, methods) from response."""
        if not tool_response:
            return {"properties": [], "methods": []}
        
        response_text = str(tool_response)
        properties = []
        methods = []
        
        # Look for property patterns
        prop_patterns = [
            r'(\\w+)\\s*:\\s*\\w+',            # TypeScript: name: string
            r'public\\s+\\w+\\s+(\\w+)\\s*\\{', # C#: public string Name {
            r'(\\w+)\\s*\\{[^}]*get',          # C#: Name { get; set; }
        ]
        
        for pattern in prop_patterns:
            matches = re.finditer(pattern, response_text, re.IGNORECASE)
            for match in matches:
                prop_name = match.group(1)
                if prop_name and prop_name[0].isupper():
                    properties.append(prop_name)
        
        # Look for method patterns
        method_patterns = [
            r'(\\w+)\\s*\\([^)]*\\)\\s*:',     # TypeScript: getName(): string
            r'public\\s+\\w+\\s+(\\w+)\\s*\\(', # C#: public string GetName(
            r'(\\w+)\\s*\\([^)]*\\)\\s*\\{',   # method() {
        ]
        
        for pattern in method_patterns:
            matches = re.finditer(pattern, response_text, re.IGNORECASE)
            for match in matches:
                method_name = match.group(1)
                if method_name and method_name[0].isupper() and method_name not in properties:
                    methods.append(method_name)
        
        return {
            "properties": list(set(properties))[:10],  # Limit to 10 each
            "methods": list(set(methods))[:10]
        }
    
    def check_auto_loading_status(self) -> dict:
        """Check if auto-loading is active."""
        return {
            "auto_loading_enabled": True,
            "csharp_auto_loaded": True,
            "typescript_auto_loaded": True
        }
    
    def handle_solution_load_success(self, tool_name: str, tool_input: Dict, state: Dict) -> str:
        """Handle successful solution/project loading."""
        auto_status = self.check_auto_loading_status()
        
        if tool_name == "csharp_load_solution":
            solution_path = tool_input.get("solutionPath", "")
            state["csharp"]["solution_loaded"] = True
            state["csharp"]["solution_path"] = solution_path
            
            # Don't show loading success for auto-loaded solutions to reduce noise
            if auto_status["auto_loading_enabled"]:
                return f"✅ C# Solution verified: {solution_path}"
            else:
                return f"✅ C# Solution loaded successfully!\\n   Path: {solution_path}\\n   CodeNav tools now ready for instant type verification"
        
        elif tool_name == "csharp_load_project":
            project_path = tool_input.get("projectPath", "")
            state["csharp"]["solution_loaded"] = True
            state["csharp"]["solution_path"] = project_path
            
            # Don't show loading success for auto-loaded projects to reduce noise
            if auto_status["auto_loading_enabled"]:
                return f"✅ C# Project verified: {project_path}"
            else:
                return f"✅ C# Project loaded successfully!\\n   Path: {project_path}\\n   CodeNav tools now ready for instant type verification"
        
        return ""
    
    def handle_type_verification_success(self, tool_name: str, tool_input: Dict, tool_response: Any, state: Dict) -> str:
        """Handle successful type verification."""
        type_name = self.extract_type_from_response(tool_response)
        if not type_name:
            return ""
        
        # Extract type members for richer feedback
        members = self.extract_type_members(tool_response)
        
        # Update session state
        if tool_name.startswith("csharp_"):
            if type_name not in state["csharp"]["verified_types"]:
                state["csharp"]["verified_types"].append(type_name)
        elif tool_name.startswith("ts_"):
            if type_name not in state["typescript"]["verified_types"]:
                state["typescript"]["verified_types"].append(type_name)
        
        # Update statistics
        state["statistics"]["total_verifications"] += 1
        
        # Generate feedback message (simplified for auto-loading environment)
        auto_status = self.check_auto_loading_status()
        feedback_lines = [f"✅ Type verified: {type_name}"]
        
        # Show key details but keep it concise since auto-loading reduces friction
        if members["properties"] and len(members["properties"]) > 0:
            props = ", ".join(members["properties"][:3])  # Reduced from 5 to 3
            if len(members["properties"]) > 3:
                props += f" (+{len(members['properties']) - 3} more)"
            feedback_lines.append(f"   Properties: {props}")
        
        if members["methods"] and len(members["methods"]) > 0:
            methods = ", ".join(members["methods"][:2])  # Reduced from 3 to 2
            if len(members["methods"]) > 2:
                methods += f" (+{len(members['methods']) - 2} more)"
            feedback_lines.append(f"   Methods: {methods}")
        
        # Simplified session context - no need to emphasize cache when auto-loading works
        total_verified = len(state["csharp"]["verified_types"]) + len(state["typescript"]["verified_types"])
        if total_verified > 5:  # Only show session count if meaningful
            feedback_lines.append(f"   Session: {total_verified} types verified")
        
        return "\\n".join(feedback_lines)
    
    def log_success_event(self, tool_name: str, tool_input: Dict, feedback: str):
        """Log successful tool usage."""
        log_entry = {
            "timestamp": datetime.utcnow().isoformat(),
            "event": "tool_success",
            "tool_name": tool_name,
            "feedback_provided": len(feedback) > 0,
            "input_summary": {
                "file_path": tool_input.get("filePath") or tool_input.get("file_path") or tool_input.get("solutionPath") or tool_input.get("projectPath"),
                "line": tool_input.get("line"),
                "column": tool_input.get("column") or tool_input.get("character")
            }
        }
        
        try:
            log_file = self.logs_dir / "guard_rails_post.json"
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
    
    def process_successful_tool_call(self, tool_name: str, tool_input: Dict, tool_response: Any, session_id: str):
        """Process a successful tool call and provide feedback."""
        
        # Load current session state
        state = self.load_session_state()
        
        # Update session ID if different
        if state["session_id"] != session_id:
            state["session_id"] = session_id
        
        feedback = ""
        
        # Handle different types of successful calls
        if tool_name in ["csharp_load_solution", "csharp_load_project"]:
            feedback = self.handle_solution_load_success(tool_name, tool_input, state)
        
        elif tool_name in ["csharp_hover", "csharp_goto_definition", "ts_hover", "ts_goto_definition"]:
            feedback = self.handle_type_verification_success(tool_name, tool_input, tool_response, state)
        
        # Save updated state
        self.save_session_state(state)
        
        # Provide feedback if we have any
        if feedback:
            print(feedback, file=sys.stderr)
            print("", file=sys.stderr)
        
        # Log the event
        self.log_success_event(tool_name, tool_input, feedback)


def main():
    parser = argparse.ArgumentParser(description="Guard Rails PostToolUse Hook")
    parser.add_argument("--workspace", type=str, help="Override workspace path")
    
    args = parser.parse_args()
    
    try:
        # Read JSON input from stdin
        input_data = json.load(sys.stdin)
        
        tool_name = input_data.get('tool_name', '')
        tool_input = input_data.get('tool_input', {})
        tool_response = input_data.get('tool_response', {})
        session_id = input_data.get('session_id', 'unknown')
        cwd = input_data.get('cwd', os.getcwd())
        
        # Only process successful responses
        if not tool_response:
            sys.exit(0)
        
        # Use provided workspace or detect from input
        workspace_path = args.workspace or cwd
        
        # Initialize hook
        hook = GuardRailsPostHook(workspace_path)
        
        # Process the successful tool call
        hook.process_successful_tool_call(tool_name, tool_input, tool_response, session_id)
        
        sys.exit(0)
        
    except json.JSONDecodeError:
        # Invalid JSON, continue silently
        sys.exit(0)
    except Exception as e:
        # Any error, continue silently
        sys.exit(0)


if __name__ == '__main__':
    main()