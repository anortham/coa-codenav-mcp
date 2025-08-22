#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#     "regex",
# ]
# ///

"""
Type Cache Builder Hook for Claude Code

This PostToolUse hook automatically builds the type verification cache by
capturing successful responses from CodeNav tools like csharp_hover, ts_hover,
csharp_goto_definition, etc. It extracts type information and stores it for
future verification checks.

Key Features:
- Automatic cache population from successful type lookups
- Extracts properties, methods, and signatures
- Supports both C# and TypeScript responses
- Integrates with Read tool for type definition files
"""

import json
import sys
import re
import os
from pathlib import Path
from typing import Dict, List, Optional, Any
from datetime import datetime
import argparse


class TypeCacheBuilder:
    """Builds and maintains the type verification cache."""
    
    def __init__(self):
        self.cache_path = Path.cwd() / ".claude" / "data" / "verified_types.json"
        self.log_path = Path.cwd() / "logs" / "type_cache_builder.json"
        
        # Ensure directories exist
        self.cache_path.parent.mkdir(parents=True, exist_ok=True)
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
    
    def load_cache(self) -> Dict:
        """Load the current verification cache."""
        if not self.cache_path.exists():
            return {"session_id": "", "verified_types": {}}
        
        try:
            with open(self.cache_path, 'r') as f:
                return json.load(f)
        except (json.JSONDecodeError, Exception):
            return {"session_id": "", "verified_types": {}}
    
    def save_cache(self, cache_data: Dict):
        """Save the verification cache."""
        try:
            with open(self.cache_path, 'w') as f:
                json.dump(cache_data, f, indent=2)
        except Exception:
            pass  # Fail silently
    
    def log_event(self, event_type: str, data: Dict):
        """Log cache building events."""
        log_entry = {
            "timestamp": datetime.utcnow().isoformat(),
            "event_type": event_type,
            "data": data
        }
        
        try:
            log_data = []
            if self.log_path.exists():
                with open(self.log_path, 'r') as f:
                    try:
                        log_data = json.load(f)
                    except json.JSONDecodeError:
                        log_data = []
            
            log_data.append(log_entry)
            
            # Keep last 500 entries
            if len(log_data) > 500:
                log_data = log_data[-500:]
            
            with open(self.log_path, 'w') as f:
                json.dump(log_data, f, indent=2)
        except Exception:
            pass
    
    def extract_type_from_hover_response(self, response_text: str) -> Optional[Dict]:
        """Extract type information from hover tool responses."""
        if not response_text:
            return None
        
        # Try to parse as JSON first (MCP response format)
        try:
            response_data = json.loads(response_text)
            if isinstance(response_data, dict):
                return self._parse_structured_response(response_data)
        except json.JSONDecodeError:
            pass
        
        # Parse as plain text response
        return self._parse_text_response(response_text)
    
    def _parse_structured_response(self, response: Dict) -> Optional[Dict]:
        """Parse structured JSON response from CodeNav tools."""
        type_info = {}
        
        # Look for common response fields
        if "symbol" in response:
            symbol = response["symbol"]
            type_info["name"] = symbol.get("name", "")
            type_info["kind"] = symbol.get("kind", "")
            type_info["signature"] = symbol.get("signature", "")
        
        if "documentation" in response:
            type_info["documentation"] = response["documentation"]
        
        if "members" in response:
            members = response["members"]
            type_info["properties"] = [m.get("name") for m in members if m.get("kind") == "Property"]
            type_info["methods"] = [m.get("signature", m.get("name")) for m in members if m.get("kind") in ["Method", "Function"]]
        
        # Look for type information in various response formats
        if "type" in response:
            type_info["type"] = response["type"]
        
        return type_info if type_info else None
    
    def _parse_text_response(self, text: str) -> Optional[Dict]:
        """Parse plain text response to extract type information."""
        type_info = {}
        lines = text.split('\n')
        
        # Look for C# signatures
        csharp_class_match = re.search(r'(public\s+)?(class|interface|struct|enum)\s+(\w+)', text, re.IGNORECASE)
        if csharp_class_match:
            type_info["name"] = csharp_class_match.group(3)
            type_info["kind"] = csharp_class_match.group(2).lower()
        
        # Look for TypeScript interfaces/types
        ts_interface_match = re.search(r'(export\s+)?(interface|type|class)\s+(\w+)', text, re.IGNORECASE)
        if ts_interface_match:
            type_info["name"] = ts_interface_match.group(3)
            type_info["kind"] = ts_interface_match.group(2).lower()
        
        # Extract properties and methods
        properties = []
        methods = []
        
        # C# property patterns
        for line in lines:
            # Properties: public string Name { get; set; }
            prop_match = re.search(r'(public|private|protected|internal)?\s*(\w+)\s+(\w+)\s*\{', line)
            if prop_match:
                properties.append(prop_match.group(3))
            
            # Methods: public void DoSomething()
            method_match = re.search(r'(public|private|protected|internal)?\s*(\w+)\s+(\w+)\s*\(', line)
            if method_match and method_match.group(3) not in properties:
                methods.append(method_match.group(3))
        
        # TypeScript property patterns
        for line in lines:
            # Properties: name: string;
            ts_prop_match = re.search(r'(\w+)\s*:\s*(\w+)', line.strip())
            if ts_prop_match and not line.strip().startswith('//'):
                properties.append(ts_prop_match.group(1))
            
            # Methods: getName(): string
            ts_method_match = re.search(r'(\w+)\s*\([^)]*\)\s*:', line.strip())
            if ts_method_match and not line.strip().startswith('//'):
                methods.append(ts_method_match.group(1))
        
        if properties:
            type_info["properties"] = list(set(properties))
        if methods:
            type_info["methods"] = list(set(methods))
        
        return type_info if type_info else None
    
    def extract_type_from_file_content(self, file_path: str, content: str) -> List[Dict]:
        """Extract type definitions from file content (for Read tool)."""
        types = []
        
        if not content:
            return types
        
        # Determine file type
        is_csharp = file_path.endswith(('.cs', '.csx'))
        is_typescript = file_path.endswith(('.ts', '.tsx'))
        
        if is_csharp:
            types.extend(self._extract_csharp_types_from_content(content, file_path))
        elif is_typescript:
            types.extend(self._extract_typescript_types_from_content(content, file_path))
        
        return types
    
    def _extract_csharp_types_from_content(self, content: str, file_path: str) -> List[Dict]:
        """Extract C# type definitions from file content."""
        types = []
        lines = content.split('\n')
        
        # Find class/interface/struct declarations
        for i, line in enumerate(lines):
            match = re.search(r'(public\s+)?(class|interface|struct|enum)\s+(\w+)', line)
            if match:
                type_info = {
                    "name": match.group(3),
                    "kind": match.group(2).lower(),
                    "file": file_path,
                    "properties": [],
                    "methods": []
                }
                
                # Look ahead for members
                brace_count = 0
                found_opening_brace = False
                
                for j in range(i, min(i + 100, len(lines))):  # Look ahead up to 100 lines
                    current_line = lines[j]
                    
                    # Track braces
                    brace_count += current_line.count('{') - current_line.count('}')
                    if '{' in current_line:
                        found_opening_brace = True
                    
                    if found_opening_brace and brace_count == 0:
                        break  # End of type definition
                    
                    # Extract properties
                    prop_match = re.search(r'(public|protected|private|internal)?\s*(\w+)\s+(\w+)\s*\{', current_line)
                    if prop_match:
                        type_info["properties"].append(prop_match.group(3))
                    
                    # Extract methods
                    method_match = re.search(r'(public|protected|private|internal)?\s*(\w+)\s+(\w+)\s*\([^)]*\)', current_line)
                    if method_match and method_match.group(3) not in type_info["properties"]:
                        type_info["methods"].append(method_match.group(3))
                
                types.append(type_info)
        
        return types
    
    def _extract_typescript_types_from_content(self, content: str, file_path: str) -> List[Dict]:
        """Extract TypeScript type definitions from file content."""
        types = []
        lines = content.split('\n')
        
        # Find interface/type/class declarations
        for i, line in enumerate(lines):
            match = re.search(r'(export\s+)?(interface|type|class)\s+(\w+)', line)
            if match:
                type_info = {
                    "name": match.group(3),
                    "kind": match.group(2).lower(),
                    "file": file_path,
                    "properties": [],
                    "methods": []
                }
                
                # Look ahead for members (similar to C# but with different syntax)
                brace_count = 0
                found_opening_brace = False
                
                for j in range(i, min(i + 100, len(lines))):
                    current_line = lines[j]
                    
                    brace_count += current_line.count('{') - current_line.count('}')
                    if '{' in current_line:
                        found_opening_brace = True
                    
                    if found_opening_brace and brace_count == 0:
                        break
                    
                    # Extract properties and methods
                    stripped_line = current_line.strip()
                    if not stripped_line or stripped_line.startswith('//'):
                        continue
                    
                    # Properties: name: string;
                    prop_match = re.search(r'(\w+)\s*:\s*[^(]+[;}]', stripped_line)
                    if prop_match:
                        type_info["properties"].append(prop_match.group(1))
                    
                    # Methods: getName(): string
                    method_match = re.search(r'(\w+)\s*\([^)]*\)\s*:', stripped_line)
                    if method_match:
                        type_info["methods"].append(method_match.group(1))
                
                types.append(type_info)
        
        return types
    
    def add_type_to_cache(self, type_info: Dict, session_id: str):
        """Add type information to the verification cache."""
        if not type_info or not type_info.get("name"):
            return
        
        cache_data = self.load_cache()
        
        # Update session if needed
        if cache_data.get("session_id") != session_id:
            cache_data["session_id"] = session_id
        
        # Ensure verified_types dict exists
        if "verified_types" not in cache_data:
            cache_data["verified_types"] = {}
        
        # Add verification timestamp
        type_info["verified_at"] = datetime.utcnow().isoformat()
        
        # Add file modification time for invalidation tracking
        source_file = type_info.get("file") or type_info.get("source_file")
        if source_file:
            try:
                source_path = Path(source_file)
                if source_path.exists():
                    type_info["file_mtime"] = source_path.stat().st_mtime
            except (OSError, ValueError):
                # If we can't get mtime, store current time as fallback
                type_info["file_mtime"] = datetime.utcnow().timestamp()
        
        # Store the type info
        cache_data["verified_types"][type_info["name"]] = type_info
        
        # Save cache
        self.save_cache(cache_data)
        
        # Log the addition
        self.log_event("type_added", {
            "type_name": type_info["name"],
            "type_kind": type_info.get("kind", "unknown"),
            "session_id": session_id
        })
    
    def process_tool_response(self, tool_name: str, tool_input: Dict, tool_response: Any, session_id: str):
        """Process a successful tool response and extract type information."""
        
        # Handle different tool types
        if tool_name in ["csharp_hover", "ts_hover"]:
            self._process_hover_response(tool_name, tool_input, tool_response, session_id)
        elif tool_name in ["csharp_goto_definition", "ts_goto_definition"]:
            self._process_definition_response(tool_name, tool_input, tool_response, session_id)
        elif tool_name == "Read":
            self._process_read_response(tool_input, tool_response, session_id)
        # Add more tool types as needed
    
    def _process_hover_response(self, tool_name: str, tool_input: Dict, tool_response: Any, session_id: str):
        """Process hover tool responses."""
        try:
            response_text = str(tool_response)
            type_info = self.extract_type_from_hover_response(response_text)
            
            if type_info:
                # If we don't have a name, try to extract from input
                if not type_info.get("name"):
                    # Look at the position in file to infer type name
                    file_path = tool_input.get("filePath") or tool_input.get("file_path", "")
                    line = tool_input.get("line", 0)
                    column = tool_input.get("column") or tool_input.get("character", 0)
                    
                    # Try to infer type name from context
                    if file_path and "User" in response_text:  # Example heuristic
                        type_info["name"] = "User"  # This could be improved
                
                if type_info.get("name"):
                    self.add_type_to_cache(type_info, session_id)
        except Exception as e:
            # Log error but don't break
            self.log_event("error", {"tool_name": tool_name, "error": str(e)})
    
    def _process_definition_response(self, tool_name: str, tool_input: Dict, tool_response: Any, session_id: str):
        """Process goto definition responses."""
        # Similar to hover but might have different structure
        self._process_hover_response(tool_name, tool_input, tool_response, session_id)
    
    def _process_read_response(self, tool_input: Dict, tool_response: Any, session_id: str):
        """Process Read tool responses to extract type definitions."""
        try:
            file_path = tool_input.get("file_path", "")
            content = str(tool_response)
            
            # Only process type definition files
            if not (file_path.endswith(('.cs', '.ts', '.tsx', '.csx')) and content):
                return
            
            # Extract types from file content
            types = self.extract_type_from_file_content(file_path, content)
            
            for type_info in types:
                self.add_type_to_cache(type_info, session_id)
                
        except Exception as e:
            self.log_event("error", {"tool_name": "Read", "error": str(e)})


def main():
    parser = argparse.ArgumentParser(description="Type Cache Builder Hook")
    parser.add_argument("--enabled", action="store_true", default=True,
                       help="Enable cache building (default: True)")
    
    args = parser.parse_args()
    
    if not args.enabled:
        sys.exit(0)
    
    try:
        # Read JSON input from stdin
        input_data = json.load(sys.stdin)
        
        tool_name = input_data.get('tool_name', '')
        tool_input = input_data.get('tool_input', {})
        tool_response = input_data.get('tool_response', {})
        session_id = input_data.get('session_id', 'default')
        
        # Only process successful responses
        if not tool_response:
            sys.exit(0)
        
        # Initialize cache builder
        builder = TypeCacheBuilder()
        
        # Process the response
        builder.process_tool_response(tool_name, tool_input, tool_response, session_id)
        
        sys.exit(0)
        
    except json.JSONDecodeError:
        # Invalid JSON, continue silently
        sys.exit(0)
    except Exception as e:
        # Any error, continue silently to avoid breaking workflow
        sys.exit(0)


if __name__ == '__main__':
    main()