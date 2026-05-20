#!/usr/bin/env python3
"""Upload Build_MCP to OSS using oss2"""
import oss2
import os
import time

# OSS credentials from environment variables
endpoint = 'oss-cn-beijing.aliyuncs.com'
access_key_id = os.environ.get('OSS_ACCESS_KEY_ID', '')
access_key_secret = os.environ.get('OSS_ACCESS_KEY_SECRET', '')
bucket_name = 'mcpkanzipublish'

auth = oss2.Auth(access_key_id, access_key_secret)
bucket = oss2.Bucket(auth, endpoint, bucket_name)

local_dir = r'C:\Users\WTY\WorkBuddy\kanziMcpServer\Build_MCP'
remote_base = 'incoming/Build_MCP'

print(f"Uploading {local_dir} to oss://{bucket_name}/{remote_base}/")

count = 0
for root, dirs, files in os.walk(local_dir):
    for filename in files:
        local_path = os.path.join(root, filename)
        rel_path = os.path.relpath(local_path, local_dir)
        remote_path = f'{remote_base}/{rel_path}'.replace('\\', '/')

        try:
            bucket.put_object_from_file(remote_path, local_path)
            print(f"  Uploaded: {remote_path}")
            count += 1
        except Exception as e:
            print(f"  FAILED: {remote_path} - {e}")

print(f"\nTotal uploaded: {count} files")

# Upload latest_build.txt
timestamp = time.strftime('%Y-%m-%d %H:%M:%S')
build_marker = f"Build completed at {timestamp}"
marker_path = os.path.join(local_dir, 'latest_build.txt')
with open(marker_path, 'w') as f:
    f.write(build_marker)

bucket.put_object_from_file(f'{remote_base}/latest_build.txt', marker_path)
print(f"Uploaded latest_build.txt")

bucket.put_object_from_file('incoming/latest_build.txt', marker_path)
print(f"Uploaded incoming/latest_build.txt")

os.remove(marker_path)
print("\nDone!")
