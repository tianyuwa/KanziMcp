#!/usr/bin/env python3
"""Download and display test result with proper encoding"""
import oss2
import os

endpoint = 'oss-cn-beijing.aliyuncs.com'
access_key_id = os.environ.get('OSS_ACCESS_KEY_ID', '')
access_key_secret = os.environ.get('OSS_ACCESS_KEY_SECRET', '')
bucket_name = 'mcpkanzipublish'

auth = oss2.Auth(access_key_id, access_key_secret)
bucket = oss2.Bucket(auth, endpoint, bucket_name)

remote_result = 'outgoing/result_latest.txt'
result_file = r'C:\Users\WTY\WorkBuddy\kanziMcpServer\test_result_full.txt'

try:
    bucket.get_object_to_file(remote_result, result_file)
    print(f"Downloaded: {result_file}")

    # Try different encodings
    for encoding in ['utf-8', 'utf-8-sig', 'gbk', 'gb2312', 'latin1']:
        try:
            with open(result_file, 'r', encoding=encoding) as f:
                content = f.read()
            print(f"\nEncoding: {encoding}")
            print("="*60)
            print(content)
            break
        except:
            continue
except Exception as e:
    print(f"Error: {e}")
