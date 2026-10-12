"""Show one factory machine in RViz with joint sliders.

    ros2 launch factory_machines_description display.launch.py model:=haas_st10
    model: haas_vf1 | haas_st10 | haas_ec500 | injection_molder
    prefix: optional TF frame prefix (e.g. "vf1/") when several machines run at once
"""
from launch import LaunchDescription
from launch.actions import DeclareLaunchArgument
from launch.conditions import IfCondition
from launch.substitutions import Command, LaunchConfiguration, PathJoinSubstitution
from launch_ros.actions import Node
from launch_ros.parameter_descriptions import ParameterValue
from launch_ros.substitutions import FindPackageShare


def generate_launch_description():
    pkg = FindPackageShare('factory_machines_description')
    model = LaunchConfiguration('model')
    urdf = PathJoinSubstitution([pkg, 'urdf', [model, '.urdf']])
    robot_description = ParameterValue(Command(['xacro ', urdf]), value_type=str)

    return LaunchDescription([
        DeclareLaunchArgument('model', default_value='haas_vf1',
                              description='haas_vf1 | haas_st10 | haas_ec500 | injection_molder'),
        DeclareLaunchArgument('gui', default_value='true', description='Start joint_state_publisher_gui'),
        DeclareLaunchArgument('rviz', default_value='true', description='Start RViz'),
        DeclareLaunchArgument('prefix', default_value='', description='TF frame prefix'),
        Node(package='robot_state_publisher', executable='robot_state_publisher', output='screen',
             parameters=[{'robot_description': robot_description,
                          'frame_prefix': LaunchConfiguration('prefix')}]),
        Node(package='joint_state_publisher_gui', executable='joint_state_publisher_gui',
             condition=IfCondition(LaunchConfiguration('gui'))),
        Node(package='rviz2', executable='rviz2', output='screen',
             arguments=['-d', PathJoinSubstitution([pkg, 'rviz', 'display.rviz'])],
             condition=IfCondition(LaunchConfiguration('rviz'))),
    ])
