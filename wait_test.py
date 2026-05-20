#!/usr/bin/env python3
"""Wait for test result from OSS, checking every 5 seconds for up to 360 seconds"""
import oss2
import time
import os

endpoint = 'oss-cn-beijing.aliyuncs.com'
access_key_id = os.environ.get('OSS_ACCESS_KEY_ID', '')
access_key_secret = os.environ.get('OSS_ACCESS_KEY_SECRET', '')
bucket_name = 'mcpkanzipublish'

auth = oss2.Auth(access_key_id, access_key_secret)
bucket = oss2.Bucket(auth, endpoint, bucket_name)

result_file = r'C:\Users\WTY\WorkBuddy\kanziMcpServer\test_result.txt'
remote_result = 'outgoing/result_latest.txt'

print("Waiting for test result...")
print(f"Will check every 5 seconds for up to 360 seconds")
print()

for i in range(72):  # 72 * 5 = 360 seconds
    try:
        # Check if file exists and get its last modified time
        try:
            meta = bucket.get_object_meta(remote_result)
            print(f"[{i*5}s] Result file exists, size={meta.content_length}, downloading...")
            bucket.get_object_to_file(remote_result, result_file)

            # Read and display the result
            with open(result_file, 'r', encoding='utf-8') as f:
                content = f.read()

            print("\n" + "="*60)
            print("TEST RESULT:")
            print("="*60)
            print(content[:3000])  # First 3000 chars

            # Check for PASS/FAIL
            if 'TEST_RESULT: PASS' in content:
                print("\n[PASS] All tests passed!")
            elif 'TEST_RESULT: FAIL' in content:
                print("\n[FAIL] Tests failed!")
            elif 'TEST_TIMEOUT' in content:
                print("\n[TIMEOUT] Test timed out!")
            else:
                print("\n[UNKNOWN] Cannot determine test result")

            # Clean up
            if os.path.exists(result_file):
                os.remove(result_file)
            break

        except oss2.exceptions.NoSuchKey:
            print(f"[{i*5}s] Result not yet available, waiting...")
            time.sleep(5)
            continue

    except Exception as e:
        print(f"[{i*5}s] Error: {e}")
        time.sleep(5)
else:
    print("\nTimeout: No result after 360 seconds")
